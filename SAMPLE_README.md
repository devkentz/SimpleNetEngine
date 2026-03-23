# NetworkEngine Sample

Gateway + GameServer 실행 샘플입니다.

## 사전 준비

### 1. Redis (k8s 환경)
이 프로젝트는 k8s에 설치된 Redis를 사용합니다.

**기본값**: `redis-dev.k8s.home:6379`

환경변수로 변경 가능:
```bash
# PowerShell
$env:REDIS_CONNECTION="your-redis-host:6379"

# Bash
export REDIS_CONNECTION="your-redis-host:6379"
```

### 2. .NET 9.0 SDK
```bash
dotnet --version  # 9.0 이상 확인
```

## 실행 방법

### Windows (PowerShell)
```powershell
.\run-sample.ps1
```

### Linux/WSL (Bash)
```bash
chmod +x run-sample.sh
./run-sample.sh
```

### 수동 실행
```bash
# 1. Gateway 실행 (터미널 1)
dotnet run --project GatewayServer/GatewayServer.csproj

# 2. GameServer 실행 (터미널 2)
dotnet run --project GameServer/GameServer.csproj

# 3. TestClient 실행 (터미널 3)
dotnet run --project Sample/TestClient/TestClient.csproj
```

## 아키텍처

```
TestClient (TCP)
    ↓
    localhost:5000
    ↓
Gateway (Dumb Proxy)
    ↓ [P2P NetMQ]
    localhost:8001 ↔ localhost:9001
    ↓
GameServer (Smart Hub/BFF)
    ↓ [Middleware Pipeline]
    ├─ Exception Handling
    ├─ Logging
    ├─ Idempotency (SequenceId)
    ├─ Performance Measurement
    └─ Business Logic
```

## 포트 구성

### Gateway
- **TCP Port 5000**: 클라이언트 연결 수신
- **P2P Port 8001**: GameServer와 P2P 통신

### GameServer
- **P2P Port 9001**: Gateway와 P2P 통신
- **Service Mesh Port 9101**: 다른 GameServer와 통신

### Redis
- **Port 6379**: 세션 저장소 (SSOT)

## 테스트 시나리오

### 1. 신규 로그인
```
Client → Gateway → GameServer
  ↓
GameServer: Redis 세션 확인 (없음)
  ↓
GameServer: 새 SessionId 생성
  ↓
GameServer: Redis에 세션 저장
  ↓
GameServer → Gateway: PinSession
  ↓
Gateway: 세션 고정 (메모리)
  ↓
Client ← Gateway ← GameServer: LOGIN_SUCCESS
```

### 2. 게임 패킷 처리
```
Client → Gateway → GameServer (고정된 세션)
  ↓
Middleware Pipeline:
  1. Exception Handling
  2. Logging
  3. Idempotency (SequenceId 체크)
  4. Performance (처리 시간 측정)
  5. Packet Handler
  ↓
Client ← Gateway ← GameServer: ECHO 응답
```

### 3. 중복 로그인 (옵션)
다른 터미널에서 동일한 UserId로 재접속하면:
- GameServer가 Redis에서 기존 세션 감지
- 기존 소켓에 DisconnectClient 전송
- 새 세션 생성 및 고정

### 4. 재접속 (옵션)
클라이언트 재접속 시:
- GameServer가 기존 SessionId 확인
- Re-pinning 또는 Reroute
- Snapshot 동기화

## 로그 확인

### Gateway 로그
```
[INFO] Client connected: SocketId={Guid}
[INFO] Session pinned: SocketId={Guid}, SessionId={Long}
[INFO] Packet received: Socket={Guid}, Size={Int}
```

### GameServer 로그
```
[INFO] GameServerHub initialized with 5 middlewares
[INFO] Received client packet: Gateway={NodeId}, Socket={Guid}
[INFO] Handling unpinned session: Socket={Guid}, UserId={Long}
[INFO] New session created: UserId={Long}, SessionId={Long}
[TRACE] Packet processed: Socket={Guid}, Elapsed={Ms}ms
```

### Idempotency 로그 (중복 패킷 감지 시)
```
[WARN] Duplicate packet detected: SessionId={Long}, SequenceId={Long}
```

## 문제 해결

### Redis 연결 실패
```
✗ Redis is NOT running
```
→ Redis를 먼저 시작하세요: `redis-server`

### 포트 충돌
```
Address already in use
```
→ 다른 프로세스가 포트를 사용 중입니다
→ Windows: `netstat -ano | findstr :5000`
→ Linux: `lsof -i :5000`

### Gateway가 GameServer에 연결 실패
```
Failed to connect to GameServer
```
→ GameServer를 먼저 실행했는지 확인
→ 방화벽 설정 확인

## 확장

### 다중 GameServer 실행
GameServerConfig.cs에서 NodeId를 다르게 설정:
```csharp
// GameServer #2
public long NodeId { get; set; } = 2;
public int P2PBindPort { get; set; } = 9002;
```

### 다중 Gateway 실행
GatewayConfig.cs에서 NodeId와 포트를 다르게 설정:
```csharp
// Gateway #2
public long GatewayNodeId { get; set; } = 2;
public int ClientPort { get; set; } = 5001;
public int P2PBindPort { get; set; } = 8002;
```

## 참고 문서
- [ARCHITECTURE.md](./docs/ARCHITECTURE.md): 전체 아키텍처
- [SESSION_LIFECYCLE.md](./docs/SESSION_LIFECYCLE.md): 세션 생명주기
- [AOP_MIDDLEWARE.md](./docs/AOP_MIDDLEWARE.md): Middleware Pattern
- [IMPLEMENTATION_PROGRESS.md](./docs/IMPLEMENTATION_PROGRESS.md): 구현 진행 상황
