# 타입 시스템

**목적:** 노드 타입의 일관된 표현
**원칙:** Single Source of Truth (SSOT)

---

## 목차
1. [EServerType 개요](#eservertype-개요)
2. [사용 위치](#사용-위치)
3. [통합 히스토리](#통합-히스토리)
4. [확장 가능성](#확장-가능성)

---

## EServerType 개요

### 정의

**위치**: `SimpleNetEngine.ProtoGenerator/proto/enums.proto (또는 Proto 정의 프로젝트)`

```protobuf
enum EServerType {
  EServerType_Gateway = 0;
  EServerType_Game = 1;
  EServerType_Api = 2;
}
```

**생성된 C# 코드**: `Internal.Protocol.EServerType`

### 목적

**두 개의 독립적인 망에서 노드 타입을 통합적으로 표현**:
- Game Session Channel: Gateway ↔ GameServer 연결
- Node Service Mesh: Gateway, GameServer, Api 서비스 간 RPC

---

## 사용 위치

### 1. Node Service Mesh

#### NodeConfig
**위치**: `SimpleNetEngine.Node/Config/NodeConfig.cs`

```csharp
public class NodeConfig
{
    public EServerType ServerType { get; set; }
    public long NodeId { get; set; }
    public string Host { get; set; }
    public int Port { get; set; }
    // ...
}
```

**용도**: Service Mesh에 참여하는 노드의 타입 식별

---

#### RemoteNode
**위치**: `SimpleNetEngine.Node/Network/RemoteNode.cs`

```csharp
public class RemoteNode
{
    public EServerType ServerType => ServerInfo.Type;
    public long Identity => ServerInfo.RemoteId;
    public string Address => serverInfo.Address;

    public string ApiName => ServerType switch
    {
        EServerType.Gateway => "Gateway",
        EServerType.Game => "Game",
        EServerType.Api => "Api",
        _ => "Unknown"
    };
}
```

**용도**: 원격 노드의 타입을 런타임에 확인

---

### 2. Game Session Channel (P2P Discovery)

#### P2PNodeInfo
**위치**: `SimpleNetEngine.Infrastructure/Discovery/P2PNodeInfo.cs`

```csharp
public record P2PNodeInfo
{
    /// <summary>
    /// 노드 유형 (Gateway, Game, Api 등)
    /// 주의: P2P Discovery는 Gateway와 Game만 지원
    /// </summary>
    public required EServerType NodeType { get; init; }
    public required long NodeId { get; init; }
    public required string P2PEndpoint { get; init; }

    public string Identity => NodeType switch
    {
        EServerType.Gateway => $"Gateway-{NodeId}",
        EServerType.Game => $"GameServer-{NodeId}",
        _ => $"{NodeType}-{NodeId}"
    };
}
```

**용도**: P2P Full Mesh에서 Gateway와 GameServer 발견

---

#### RedisP2PDiscoveryService
**위치**: `SimpleNetEngine.Infrastructure/Discovery/RedisP2PDiscoveryService.cs`

```csharp
public class RedisP2PDiscoveryService : IP2PDiscoveryService
{
    public async Task<List<P2PNodeInfo>> RegisterAndDiscoverAsync(
        P2PNodeInfo selfInfo, TimeSpan ttl)
    {
        await RegisterNodeAsync(selfInfo, ttl);

        // 상대방 노드 타입 결정
        var targetType = selfInfo.NodeType == EServerType.Gateway
            ? EServerType.Game
            : EServerType.Gateway;

        return await DiscoverNodesAsync(targetType);
    }

    private static string GetKeyPrefix(EServerType nodeType)
    {
        return nodeType switch
        {
            EServerType.Gateway => "p2p:gateway:",
            EServerType.Game => "p2p:gameserver:",
            _ => throw new ArgumentException(
                $"P2P Discovery는 Gateway와 Game만 지원합니다: {nodeType}")
        };
    }
}
```

**용도**: Redis에서 타입별 노드 조회

---

### 3. 서버 Config

#### GatewayConfig
**위치**: `SimpleNetEngine.Gateway/Options/GatewayOptions.cs`

```csharp
public class GatewayConfig
{
    public IOptions<NodeConfig> ToNodeConfig()
    {
        var nodeConfig = new NodeConfig
        {
            NodeGuid = this.NodeGuid,
            Port = this.ServiceMeshPort,
            ServerType = EServerType.Gateway,  // ← 여기서 설정
            Host = "127.0.0.1"
        };

        return Options.Create(nodeConfig);
    }
}
```

---

#### GameServerConfig
**위치**: `SimpleNetEngine.Game/Options/GameOptions.cs`

```csharp
public class GameServerConfig
{
    public IOptions<NodeConfig> ToNodeConfig()
    {
        var nodeConfig = new NodeConfig
        {
            NodeGuid = this.NodeGuid,
            Port = this.ServiceMeshPort,
            ServerType = EServerType.Game,  // ← 여기서 설정
            Host = "127.0.0.1"
        };

        nodeConfig.Metadata[NodeMetadataKeys.UserPacketMeshPort]
            = this.P2PBindPort.ToString();

        return Options.Create(nodeConfig);
    }
}
```

---

## 통합 히스토리

### Before: P2PNodeType (제거됨)

**문제점**:
```csharp
// 두 개의 유사한 enum이 존재
public enum P2PNodeType {
    Gateway,
    GameServer
}

public enum EServerType {
    Gateway,
    Game,
    Api
}
```

**문제**:
- Gateway와 GameServer가 두 가지 enum으로 표현됨
- 타입 변환 시 실수 가능성
- 유지보수 비용 증가

---

### After: EServerType 통합

**해결**:
```csharp
// 단일 타입으로 통합
public record P2PNodeInfo {
    public required EServerType NodeType { get; init; }  // ✅
}

public class NodeConfig {
    public EServerType ServerType { get; set; }  // ✅
}
```

**장점**:
- ✅ 단일 진실 원천 (SSOT)
- ✅ Gateway/Game가 일관되게 표현됨
- ✅ 확장 가능 (Api도 P2P 참여 가능)

---

### 마이그레이션 상세

**변경 사항**:
1. `P2PNodeInfo.NodeType`: `P2PNodeType` → `EServerType`
2. `IP2PDiscoveryService`: 시그니처 변경
3. `RedisP2PDiscoveryService`: switch문 업데이트
4. `SimpleNetEngine.Infrastructure.csproj`: ProtoGenerator 참조 추가

**Breaking Changes**: 없음
- enum 값이 동일하게 매핑됨 (Gateway=0, Game=1)
- Redis 데이터 호환성 유지

**관련 문서**: [타입 통합 계획서](../planning/2024-12-type-unification.md)

---

## 확장 가능성

### Api 서버의 P2P Discovery 참여 (미래)

현재는 Gateway ↔ GameServer만 P2P Discovery를 사용하지만, 향후 Api 서버도 참여 가능:

```csharp
// 현재
private static string GetKeyPrefix(EServerType nodeType) {
    return nodeType switch {
        EServerType.Gateway => "p2p:gateway:",
        EServerType.Game => "p2p:gameserver:",
        _ => throw new ArgumentException($"지원 안 함: {nodeType}")
    };
}

// 향후 확장
private static string GetKeyPrefix(EServerType nodeType) {
    return nodeType switch {
        EServerType.Gateway => "p2p:gateway:",
        EServerType.Game => "p2p:gameserver:",
        EServerType.Api => "p2p:apiserver:",  // ✅ 추가 가능
        _ => throw new ArgumentException($"지원 안 함: {nodeType}")
    };
}
```

---

### 새로운 서버 타입 추가

**Step 1**: proto 파일 업데이트
```protobuf
enum EServerType {
  EServerType_Gateway = 0;
  EServerType_Game = 1;
  EServerType_Api = 2;
  EServerType_Chat = 3;  // ✅ 새로운 타입
}
```

**Step 2**: Protobuf 코드 재생성
```bash
dotnet build
```

**Step 3**: 필요한 곳에서 switch 케이스 추가
```csharp
public string ApiName => ServerType switch
{
    EServerType.Gateway => "Gateway",
    EServerType.Game => "Game",
    EServerType.Api => "Api",
    EServerType.Chat => "Chat",  // ✅ 추가
    _ => "Unknown"
};
```

---

## 타입 사용 패턴

### 1. Service Mesh에서 타입 필터링

```csharp
// Gateway에서 GameServer만 연결
public class GatewayNodeEventController : StatelessEventController
{
    public override void OnJoinNode(RemoteNode remoteNode)
    {
        if (remoteNode.ServerType == EServerType.Game)
        {
            // GameServer와 P2P 연결 설정
            if (remoteNode.ServerInfo.Metadata.TryGetValue(
                NodeMetadataKeys.UserPacketMeshPort, out var portStr))
            {
                var endpoint = $"tcp://{ip}:{portStr}";
                _packetRouter.ConnectToGameServer(remoteNode.Identity, endpoint);
            }
        }
    }
}
```

---

### 2. P2P Discovery에서 상대 타입 결정

```csharp
// Gateway는 GameServer 찾기, GameServer는 Gateway 찾기
var targetType = selfInfo.NodeType == EServerType.Gateway
    ? EServerType.Game
    : EServerType.Gateway;

var peers = await _discoveryService.DiscoverNodesAsync(targetType);
```

---

### 3. 타입별 라우팅

```csharp
public async Task<IMessage> RouteToService(long userId, IMessage request)
{
    // 타입에 따라 다른 서비스로 라우팅
    var targetType = GetServiceType(request);

    var nodes = _nodeManager.GetNodesByType(targetType);
    var targetNode = SelectNode(nodes, userId);

    return await _nodeSender.SendAsync(targetNode.Identity, request);
}
```

---

## 관련 문서

- [타입 통합 계획서](../planning/2024-12-type-unification.md)
- [Game Session Channel](02-user-packet-mesh.md)
- [Node Service Mesh](03-node-service-mesh.md)
- [라이브러리 구조](04-library-structure.md)
