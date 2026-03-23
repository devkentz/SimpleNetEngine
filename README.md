# SimpleNetEngine

.NET 9 기반 TCP 게임 서버 프레임워크. NetCoreServer(클라이언트 통신) + NetMQ(서버 간 통신)로 구성.

## 아키텍처

```
Client ──TCP──► Gateway (Dumb Proxy) ──NetMQ P2P──► GameServer (Smart Hub)
                                                         │
                                                    NetMQ Mesh
                                                         │
                                                  Stateless Services
```

- **Game Session Channel (Data Plane)**: Client ↔ Gateway ↔ GameServer — 유저 패킷 전용
- **Node Service Mesh (Control Plane)**: 서버 간 RPC 통신 — Full Mesh 토폴로지

상세: [docs/architecture/01-overview.md](docs/architecture/01-overview.md)

## 기술 스택

| 구분 | 기술 |
|------|------|
| 런타임 | .NET 9, C# 13 |
| 클라이언트 통신 | NetCoreServer (TCP) |
| 서버 간 통신 | NetMQ (ZeroMQ) |
| 직렬화 | Protocol Buffers |
| 세션 관리 | Redis (SSOT) |
| 로깅 | Serilog + OpenTelemetry |
| 스트레스 테스트 | DFrame |

## 주요 기능

### Controller 기반 패킷 핸들링

ASP.NET 스타일의 어트리뷰트 기반 패킷 핸들러:

```csharp
[NodeController]
public class LoginController(ISessionManager sessions)
{
    [NodePacketHandler(MessageId.LoginRequest)]
    public async ValueTask HandleLogin(NodePacket packet)
    {
        // ...
    }
}
```

### Gateway / GameServer 분리

| 서버 | 역할 |
|------|------|
| Gateway | TCP I/O, 패킷 포워딩, 암호화/복호화, 압축/해제 |
| GameServer | 패킷 파싱, 세션 검증, 비즈니스 로직 |

### 선택적 암호화

- ECDH P-256 키 교환 + ECDSA P-256 서명 (MITM 방어)
- AES-256-GCM 세션 암호화
- 패킷 단위 선택적 적용 (Flags 기반)

상세: [docs/ENCRYPTION_COMPRESSION_DESIGN.md](docs/ENCRYPTION_COMPRESSION_DESIGN.md)

### Service Discovery & Scale-Out

- Redis 기반 Service Discovery — 서버 목록을 Redis에 등록/조회
- 주기적 Heartbeat로 서버 상태 감시
- 신규 서버 추가 시 자동 감지 및 연결, 장애 서버 자동 제거
- Gateway/GameServer/Stateless Service 모두 수평 확장 가능


상세: [docs/NODE_CONCURRENCY_MODELS.md](docs/NODE_CONCURRENCY_MODELS.md)

### 성능 최적화

- **송신/수신 처리 경로 분리**: NetMQ Poller(수신) + Channel\<T\>(송신)로 상호 블로킹 제거
- **Zero-Copy**: `StructLayout(Pack=1)` + `MemoryMarshal.AsRef<T>` 헤더 직접 읽기
- **메모리 재사용**: `ArrayPool<byte>`, NetMQ `Msg.Move()` 소유권 이전

상세: [docs/NETMQ_OPTIMIZATION.md](docs/NETMQ_OPTIMIZATION.md)

## 프로젝트 구조

```
SimpleNetEngine.Protocol       (Tier 0)  패킷 정의, 메모리 유틸리티
SimpleNetEngine.Infrastructure (Tier 1)  분산 시스템 인프라 (Redis, NetMQ)
SimpleNetEngine.Node           (Tier 2)  Service Mesh 구현
SimpleNetEngine.Gateway        (Tier 3)  Gateway 라이브러리
SimpleNetEngine.Game           (Tier 3)  GameServer 라이브러리
Sample/                                  실행 가능한 샘플 서버
```

## 벤치마크

### Echo 스트레스 테스트

DFrame 기반, Worker 3대, Concurrency 600, Total Request 3,000,000

| Worker | Succeed | Error | Avg(ms) | Median(ms) | P90(ms) | P95(ms) | RPS |
|--------|---------|-------|---------|------------|---------|---------|-----|
| 1 | 1,000,000 | 0 | 31.99 | 20.61 | 43.12 | 53.00 | 18,582 |
| 2 | 1,000,000 | 0 | 28.40 | 23.56 | 46.37 | 71.22 | 20,827 |
| 3 | 1,000,000 | 0 | 32.27 | 19.02 | 40.70 | 55.52 | 18,538 |
| **합계** | **3,000,000** | **0** | - | - | - | - | **57,949** |


## 빌드 및 실행

```bash
# 전체 빌드
dotnet build SimpleNetEngine.sln

# Aspire AppHost로 실행 (Gateway + GameServer + Redis)
dotnet run --project Sample/Sample.AppHost
```

## 문서

| 문서 | 설명 |
|------|------|
| [아키텍처 개요](docs/architecture/01-overview.md) | 전체 시스템 구조 |
| [Game Session Channel](docs/architecture/02-game-session-channel.md) | Data Plane 상세 |
| [Node Service Mesh](docs/architecture/03-node-service-mesh.md) | Control Plane 상세 |
| [패킷 구조](docs/architecture/07-packet-structure.md) | 패킷 헤더/바디 명세 |
| [암호화/압축](docs/ENCRYPTION_COMPRESSION_DESIGN.md) | 선택적 암호화 설계 |
| [동시성 모델](docs/NODE_CONCURRENCY_MODELS.md) | Sequential/Parallel/Actor |
| [NetMQ 최적화](docs/NETMQ_OPTIMIZATION.md) | Zero-Copy, 성능 튜닝 |
