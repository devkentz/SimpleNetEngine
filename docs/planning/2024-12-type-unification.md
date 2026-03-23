# 타입 시스템 통합 계획서 (EServerType 통합)

**작성일:** 2024-12-01
**상태:** ✅ 완료
**커밋:** `b9f7b21`

---

## 1. 배경 및 문제점

### 현재 상황
시스템에 두 개의 유사한 enum이 존재:

#### EServerType (Protocol Buffers)
- **위치**: `SimpleNetEngine.ProtoGenerator/proto/enums.proto`
- **값**: `Gateway(0)`, `Game(1)`, `Api(2)`
- **목적**: Service Mesh 노드 타입 식별
- **사용 횟수**: 34회
- **계층**: SimpleNetEngine.Node (Tier 2) 이상

#### P2PNodeType (C# enum)
- **위치**: `SimpleNetEngine.Infrastructure/Discovery/P2PNodeInfo.cs`
- **값**: `Gateway`, `GameServer`
- **목적**: P2P Full Mesh Discovery
- **사용 횟수**: 13회
- **계층**: SimpleNetEngine.Infrastructure (Tier 1)

### 문제점
1. **타입 중복**: Gateway와 GameServer가 두 가지 enum으로 표현됨
2. **일관성 부족**: Service Mesh와 P2P Mesh 모두 사용하는 노드인데 타입이 분리됨
3. **실수 가능성**: 타입 변환 시 오류 발생 가능
4. **유지보수 비용**: 동일한 개념을 두 번 관리

---

## 2. 차이점 분석

| 측면 | EServerType | P2PNodeType |
|------|-------------|-------------|
| **정의 방식** | Protocol Buffers (언어 중립) | C# enum |
| **값 종류** | Gateway, Game, Api | Gateway, GameServer |
| **통신 레이어** | Node Service Mesh (Control) | Game Session Channel (Data) |
| **의존성 계층** | Protocol → Node (상위) | Infrastructure (하위) |
| **확장 가능성** | Api 타입 포함 (3가지) | Gateway/GameServer만 (2가지) |
| **직렬화** | Protobuf 직렬화 | JSON/메모리 |

---

## 3. 통합 시나리오 비교

### ✅ 시나리오 A: EServerType으로 통합 (선택됨)

**방법:**
1. P2PNodeType enum 삭제
2. P2PNodeInfo.NodeType을 `EServerType`로 변경
3. RedisP2PDiscoveryService에서 `EServerType.Gateway` / `EServerType.Game` 사용

**장점:**
- ✅ 단일 진실 원천 (Single Source of Truth)
- ✅ Gateway/Game 노드가 두 통신 레이어 모두 사용 → 일관성 향상
- ✅ 향후 Api 서버도 P2P Discovery 참여 가능
- ✅ Protocol Buffers 기반이라 다른 언어 지원 용이

**단점:**
- ⚠️ Infrastructure가 ProtoGenerator에 의존 (계층 변화)
- ⚠️ EServerType.Api가 P2P Discovery에 불필요하게 노출됨 (switch로 예외 처리)

**Breaking Changes:**
- ✅ 없음! enum 값이 동일하게 매핑됨 (Gateway=0, Game=1)

---

### ❌ 시나리오 B: P2PNodeType으로 통합 (비선택)

**문제점:**
- ❌ Protocol Buffers에서 EServerType 제거 불가 (내부 프로토콜 사용)
- ❌ Api 서버 타입 표현 불가
- ❌ 계층 역전: Node가 Infrastructure enum에 의존

---

### ⚖️ 시나리오 C: 현상 유지 (비선택)

**장점:**
- ✅ 명확한 책임 분리
- ✅ Infrastructure가 Protocol에 의존하지 않음

**단점:**
- ⚠️ 타입 중복
- ⚠️ 실수 가능성

---

## 4. 구현 계획

### Phase 1: Infrastructure 업데이트
```xml
<!-- SimpleNetEngine.Infrastructure.csproj -->
<ItemGroup>
  <ProjectReference Include="..\SimpleNetEngine.Protocol\SimpleNetEngine.Protocol.csproj" />
  <ProjectReference Include="..\SimpleNetEngine.ProtoGenerator\SimpleNetEngine.ProtoGenerator.csproj" />
</ItemGroup>
```

### Phase 2: P2PNodeInfo 수정
```csharp
// Before
public record P2PNodeInfo {
    public required P2PNodeType NodeType { get; init; }
}

// After
using Internal.Protocol;

public record P2PNodeInfo {
    public required EServerType NodeType { get; init; }
}
```

### Phase 3: Discovery 서비스 업데이트
```csharp
// RedisP2PDiscoveryService.cs
private static string GetKeyPrefix(EServerType nodeType) {
    return nodeType switch {
        EServerType.Gateway => GatewayKeyPrefix,
        EServerType.Game => GameServerKeyPrefix,
        _ => throw new ArgumentException(
            $"P2P Discovery는 Gateway와 Game만 지원합니다: {nodeType}")
    };
}
```

### Phase 4: Identity 생성 로직 개선
```csharp
// P2PNodeInfo.cs
public string Identity => NodeType switch {
    EServerType.Gateway => $"Gateway-{NodeId}",
    EServerType.Game => $"GameServer-{NodeId}",
    _ => $"{NodeType}-{NodeId}"
};
```

---

## 5. 실행 결과

### 변경 통계
```
9 files changed, 31 insertions(+), 27 deletions(-)
```

### 변경된 파일
1. `SimpleNetEngine.Infrastructure/Discovery/P2PNodeInfo.cs` - P2PNodeType 제거
2. `SimpleNetEngine.Infrastructure/Discovery/IP2PDiscoveryService.cs` - 시그니처 변경
3. `SimpleNetEngine.Infrastructure/Discovery/RedisP2PDiscoveryService.cs` - EServerType 사용
4. `SimpleNetEngine.Infrastructure/SimpleNetEngine.Infrastructure.csproj` - ProtoGenerator 참조 추가
5. 기타 using 문 추가 파일들

### 검증 결과
- ✅ 전체 빌드: 경고 0개, 오류 0개
- ✅ 유닛 테스트: 18/18 통과
- ✅ Breaking Changes: 없음 (enum 값 호환)

---

## 6. enum 값 매핑

### P2PNodeType → EServerType
```
P2PNodeType.Gateway    (0) → EServerType.Gateway (0)  ✅ 호환
P2PNodeType.GameServer (1) → EServerType.Game    (1)  ✅ 호환
```

### Redis 데이터 호환성
```json
// Before
{
  "NodeType": 0,  // P2PNodeType.Gateway
  "NodeId": 1
}

// After
{
  "NodeType": 0,  // EServerType.Gateway (동일한 값!)
  "NodeId": 1
}
```

**결론**: Redis에 저장된 기존 데이터와 완벽 호환

---

## 7. 계층 의존성 변화

### Before
```
Protocol (T0) ← Infrastructure (T1) ← Node (T2)
```

### After
```
Protocol (T0) ← Infrastructure (T1) ← Node (T2)
      ↑                ↑
ProtoGenerator ───────┘
(컴파일 타임만)
```

**참고:**
- ProtoGenerator는 컴파일 타임 의존성 (런타임 아님)
- Infrastructure는 이미 Google.Protobuf 패키지 사용 중
- 순환 의존성 없음

---

## 8. 마이그레이션 체크리스트

- [x] SimpleNetEngine.Infrastructure.csproj에 ProtoGenerator 참조 추가
- [x] P2PNodeInfo.NodeType을 EServerType으로 변경
- [x] P2PNodeType enum 삭제
- [x] IP2PDiscoveryService 인터페이스 시그니처 변경
- [x] RedisP2PDiscoveryService 구현 업데이트
- [x] Identity 생성 로직 명시적 매핑
- [x] 전체 빌드 검증
- [x] 유닛 테스트 실행
- [x] Redis 데이터 호환성 확인

---

## 9. 향후 확장 가능성

### Api 서버의 P2P Discovery 참여
현재는 Gateway ↔ GameServer만 P2P Discovery를 사용하지만, 향후 Api 서버도 참여 가능:

```csharp
private static string GetKeyPrefix(EServerType nodeType) {
    return nodeType switch {
        EServerType.Gateway => GatewayKeyPrefix,
        EServerType.Game => GameServerKeyPrefix,
        EServerType.Api => ApiServerKeyPrefix,  // 추가 가능
        _ => throw new ArgumentException($"지원하지 않는 노드 타입: {nodeType}")
    };
}
```

---

## 10. 얻은 교훈

### 성공 요인
1. **철저한 분석**: 두 타입의 사용처와 목적을 명확히 파악
2. **호환성 우선**: enum 값 매핑을 통한 Breaking Change 회피
3. **단계적 접근**: 계층 의존성 변화에 대한 충분한 검토
4. **테스트 검증**: 변경 후 즉시 테스트 실행

### 설계 결정
1. **SSOT 원칙**: 동일한 개념은 하나의 타입으로 표현
2. **확장성 고려**: Api 서버 등 향후 추가 노드 타입 대비
3. **명시적 매핑**: Identity 생성 시 switch문으로 명확히 매핑

### 향후 적용
- 새로운 노드 타입 추가 시 EServerType에만 정의
- P2P Discovery 지원 여부는 switch문에서 제어
- 타입 통합으로 일관성 유지

---

## 11. 타임라인

| Phase | 작업 내용 | 소요 시간 |
|-------|-----------|-----------|
| 분석 | 타입 사용처 파악, 시나리오 비교 | 30분 |
| 구현 | 코드 변경, 빌드 검증 | 30분 |
| 테스트 | 유닛 테스트, 호환성 확인 | 20분 |
| **총합** | | **1시간 20분** |

---

## 12. 참고 자료

### 주요 파일
- `SimpleNetEngine.ProtoGenerator/proto/enums.proto` - EServerType 정의
- `SimpleNetEngine.Infrastructure/Discovery/P2PNodeInfo.cs` - 통합된 타입 사용
- `SimpleNetEngine.Infrastructure/Discovery/RedisP2PDiscoveryService.cs` - Discovery 구현

### 관련 커밋
- `b9f7b21` - refactor: P2PNodeType을 EServerType으로 통합

### 문서
- [타입 시스템](../architecture/05-type-system.md)
- [Game Session Channel](../architecture/02-user-packet-mesh.md)
- [Node Service Mesh](../architecture/03-node-service-mesh.md)
