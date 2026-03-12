# Sample - SimpleNetEngine 데모

SimpleNetEngine 라이브러리를 사용하는 샘플 프로젝트입니다.
Gateway + GameServer + NodeSample + TestClient 구성으로 Echo 요청/응답 및 Service Mesh RPC 플로우를 시연합니다.

## 프로젝트 구성

| 프로젝트 | 역할 |
|----------|------|
| **Sample.AppHost** | .NET Aspire 오케스트레이터 (Redis + Gateway×2 + GameServer×2 + NodeSample×3 일괄 실행) |
| **GatewaySample** | Gateway 서버 — TCP 소켓 관리, 패킷 전달 (Dumb Proxy) |
| **GameSample** | GameServer — Actor 기반 유저 세션 처리, EchoController 포함 |
| **NodeSample** | Stateless Service — Service Mesh 기반 RPC 처리 (Parallel 워커) |
| **TestClient** | 콘솔 클라이언트 — Redis Gateway 자동 탐색, 시나리오 기반 테스트 |
| **Protocol.Sample.User** | Protobuf 메시지 정의 — Client ↔ GameServer (EchoReq, NodeEchoReq 등) |
| **Protocol.Sample.Node** | Protobuf 메시지 정의 — Service Mesh RPC (ServiceMeshEchoReq 등) |

## 실행 방법

### Aspire로 실행 (권장)

```bash
cd Sample/Sample.AppHost
dotnet run
```

- Redis 서버 자동 시작 (port 6379, TLS/비밀번호 없음)
- Gateway×2 + GameServer×2 + NodeSample×3 자동 실행
- Aspire Dashboard에서 로그/트레이스/메트릭 확인 가능

### 개별 실행

```bash
# 1. Redis 실행
# 2. Gateway
cd Sample/GatewaySample && dotnet run
# 3. GameServer
cd Sample/GameSample && dotnet run
# 4. NodeSample (Stateless Service)
cd Sample/NodeSample && dotnet run
# 5. Client
cd Sample/TestClient && dotnet run [redisConn] [registryKey] [pubKeyPath]
```

## 테스트 시나리오 (TestClient)

TestClient는 Redis에서 Gateway 목록을 자동 탐색하며, 다음 시나리오를 지원합니다:

| # | 시나리오 | 설명 |
|---|----------|------|
| 1 | **Echo** | 기본 연결 + 핸드셰이크 + 로그인 + Echo 요청/응답 |
| 2 | **Node Echo** | Client → Gateway → GameServer → NodeSample (Service Mesh RPC) → 응답 |
| 3 | **Duplicate Login** | 동일 credential 중복 로그인 시 기존 클라이언트 kick-out 검증 |
| 4 | **Inactivity Timeout** | idle ping 비활성화 후 서버 타임아웃(30s) 동작 검증 |
| 5 | **Reconnect** | 강제 Disconnect 후 ReconnectKey 기반 재연결 (Cross-Node 포함) |

## Echo 플로우

### User Echo (시나리오 1)
```
Client → Gateway → GameServer(Actor) → EchoController → GameServer → Gateway → Client
```

### Node Echo (시나리오 2)
```
Client → Gateway → GameServer(Actor) → Service Mesh → NodeSample → GameServer → Gateway → Client
```

1. **연결**: Client가 Gateway에 TCP 연결 (Redis에서 Gateway 목록 Round-Robin 선택)
2. **핸드셰이크**: ECDH P-256 키 교환 + ECDSA 서명 검증 → AES-256-GCM 암호화 활성화
3. **로그인**: LoginGameReq → Redis 세션 등록 → Actor 활성화
4. **Echo**: EchoReq (암호화) → Gateway 전달 → Actor Mailbox → EchoController → EchoRes (암호화)
5. **Node Echo**: NodeEchoReq → GameServer → INodeSender.RequestApiAsync → NodeSample → 응답

## 프로토콜 정의

### Protocol.Sample.User (`sample_user.proto`)

```protobuf
// Client ↔ GameServer
message EchoReq     { string message = 1; int32 timestamp = 2; }
message EchoRes     { string message = 1; int32 timestamp = 2; }
message NodeEchoReq { string message = 1; int32 timestamp = 2; }
message NodeEchoRes { string message = 1; int32 timestamp = 2; }
```

### Protocol.Sample.Node (`sample_node.proto`)

```protobuf
// Service Mesh RPC (GameServer ↔ NodeSample)
message ServiceMeshEchoReq { string message = 1; }
message ServiceMeshEchoRes { string message = 1; int64 node_id = 2; int64 timestamp = 3; }
```

## 인증서

`certs/` 디렉토리에 개발용 ECDSA P-256 키쌍이 포함되어 있습니다.

- `server_signing_key.pem` — 서버 서명용 개인키 (개발 전용)
- `server_signing_key.pub.pem` — 클라이언트 검증용 공개키

