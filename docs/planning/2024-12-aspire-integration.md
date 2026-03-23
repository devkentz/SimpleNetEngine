# .NET Aspire 통합 계획

> **작성일**: 2026-03-03
> **목적**: NetworkEngine.Merged 프로젝트에 .NET Aspire 도입하여 로컬 개발 환경 개선 및 분산 서비스 오케스트레이션

---

## 📋 현재 상태 분석

### 프로젝트 구조

```
Sample/
├── GatewaySample/          # Gateway 서버 (새 버전)
│   ├── TCP: 5000
│   ├── GameSessionChannel: 6000
│   ├── ServiceMesh: 7000
│   └── Redis: redis-dev.k8s.home
├── GameSample/             # GameServer (새 버전)
│   ├── GameSessionChannel: 9001
│   ├── ServiceMesh: 9101
│   └── Redis: redis-dev.k8s.home:6379
├── TestClient/             # 테스트 클라이언트
├── GatewayServer/          # Gateway 서버 (구 버전)
└── GameServer/             # GameServer (구 버전)
```

### 현재 문제점

1. **수동 실행**: 각 서비스를 개별적으로 실행해야 함
2. **포트 충돌**: 여러 인스턴스 실행 시 포트 관리 복잡
3. **Redis 의존성**: 외부 Redis 서버 필요 (redis-dev.k8s.home)
4. **모니터링 부재**: 분산 서비스 간 통신 추적 어려움
5. **설정 관리**: appsettings.json이 각 프로젝트에 분산
6. **Service Discovery**: NetMQ P2P는 IP:Port 하드코딩

### Aspire 도입 시 개선점

✅ **통합 실행**: AppHost에서 모든 서비스 한 번에 시작
✅ **동적 포트**: Aspire가 자동으로 포트 할당
✅ **로컬 Redis**: Aspire Container로 Redis 자동 시작
✅ **Dashboard**: 실시간 로그, 메트릭, 트레이싱
✅ **중앙 설정**: 환경 변수로 설정 주입
✅ **Health Check**: 서비스 상태 모니터링

---

## 🎯 Aspire 통합 목표

### Phase 1: 최소 구성 (P0 - 1주)

- [ ] Aspire AppHost 프로젝트 생성
- [ ] Redis 컨테이너 통합
- [ ] GatewaySample Aspire 등록
- [ ] GameSample Aspire 등록 (단일 인스턴스)
- [ ] Dashboard 실행 및 검증

### Phase 2: 개발 환경 개선 (P1 - 1주)

- [ ] ServiceDefaults 프로젝트 생성 (공통 설정)
- [ ] Health Check 통합
- [ ] OpenTelemetry 추적 추가
- [ ] 환경별 설정 (Development/Staging)

### Phase 3: 고급 기능 (P2 - 2주)

- [ ] GameServer 다중 인스턴스 (Replica)
- [ ] Service-to-Service Discovery
- [ ] 배포 매니페스트 생성 (Azure Container Apps)
- [ ] CI/CD 통합

---

## 📐 아키텍처 설계

### Aspire 프로젝트 구조

```
Sample/
├── Sample.AppHost/                    # ⭐ 새로 생성
│   ├── Program.cs                     # 오케스트레이터
│   ├── appsettings.json               # 전역 설정
│   └── Sample.AppHost.csproj
│
├── Sample.ServiceDefaults/            # ⭐ 새로 생성 (선택적)
│   ├── Extensions.cs                  # 공통 확장 메서드
│   └── Sample.ServiceDefaults.csproj
│
├── GatewaySample/                     # ✏️ 수정
│   ├── Program.cs                     # Aspire 클라이언트 추가
│   └── GatewaySample.csproj           # Aspire 패키지 참조
│
├── GameSample/                        # ✏️ 수정
│   ├── Program.cs                     # Aspire 클라이언트 추가
│   └── GameSample.csproj              # Aspire 패키지 참조
│
└── TestClient/                        # ✏️ 수정 (선택적)
```

### AppHost Program.cs 구조

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// 1. Redis (Container)
var redis = builder.AddRedis("redis")
    .WithDataVolume()  // 데이터 영속화
    .WithRedisCommander();  // GUI 관리 도구

// 2. Gateway (단일 인스턴스)
var gateway = builder.AddProject<Projects.GatewaySample>("gateway")
    .WithReference(redis)
    .WithEnvironment("Gateway__TcpPort", "5000")
    .WithEnvironment("Gateway__GameSessionChannelPort", "6000")
    .WithEnvironment("Gateway__ServiceMeshPort", "7000")
    .WithHttpEndpoint(port: 5000, name: "tcp");

// 3. GameServer (단일 인스턴스, 추후 Replica)
var gameServer = builder.AddProject<Projects.GameSample>("gameserver")
    .WithReference(redis)
    .WithEnvironment("GameServer__GameSessionChannelPort", "9001")
    .WithEnvironment("GameServer__ServiceMeshPort", "9101")
    .WithReplicas(1);  // Phase 3에서 증가 가능

// 4. TestClient (선택적)
builder.AddProject<Projects.TestClient>("testclient")
    .WithReference(gateway);

builder.Build().Run();
```

---

## 🔧 상세 구현 계획

### Step 1: Aspire CLI 설치 및 템플릿 설치

```bash
# Aspire Workload 설치
dotnet workload install aspire

# 버전 확인
dotnet workload list
```

### Step 2: AppHost 프로젝트 생성

```bash
cd Sample

# AppHost 프로젝트 생성
dotnet new aspire-apphost -n Sample.AppHost

# 솔루션에 추가
cd ..
dotnet sln NetworkEngine.sln add Sample/Sample.AppHost/Sample.AppHost.csproj

# 기존 프로젝트 참조 추가
cd Sample/Sample.AppHost
dotnet add reference ../GatewaySample/GatewaySample.csproj
dotnet add reference ../GameSample/GameSample.csproj
dotnet add reference ../TestClient/TestClient.csproj
```

### Step 3: ServiceDefaults 프로젝트 생성 (선택적)

```bash
cd Sample

# ServiceDefaults 프로젝트 생성
dotnet new aspire-servicedefaults -n Sample.ServiceDefaults

# 솔루션에 추가
cd ..
dotnet sln NetworkEngine.sln add Sample/Sample.ServiceDefaults/Sample.ServiceDefaults.csproj
```

### Step 4: GatewaySample에 Aspire 통합

**GatewaySample.csproj 수정**:
```xml
<ItemGroup>
  <PackageReference Include="Aspire.Hosting.Redis" />
  <ProjectReference Include="..\Sample.ServiceDefaults\Sample.ServiceDefaults.csproj" />
</ItemGroup>
```

**GatewaySample/Program.cs 수정** (TCP 앱 + Aspire):
```csharp
using Microsoft.Extensions.ServiceDiscovery;

var builder = WebApplication.CreateBuilder(args);

// ⭐ Aspire ServiceDefaults: OTLP, Metrics, Logging, Health Check
builder.AddServiceDefaults();

// ⭐ Redis 연결 문자열 자동 주입
builder.AddRedisClient("redis");

// 기존 Gateway 설정 로드
var config = builder.Configuration.GetSection("Gateway").Get<GatewayConfig>()
    ?? throw new InvalidOperationException("Gateway config not found");

// Gateway 서비스 등록 (기존 NetMQ 기반)
builder.Services.AddSingleton<GatewayTcpServer>();
builder.Services.AddSingleton<P2PGameServerBus>();
builder.Services.AddSingleton<GamePacketRouter>();

// ⭐ Custom Health Check: TCP 서버 상태 확인
builder.Services.AddHealthChecks()
    .AddCheck<GatewayHealthCheck>("gateway_tcp");

var app = builder.Build();

// ⭐ Health Check HTTP 엔드포인트 (별도 포트, 예: 5001)
// GET /health → {"status": "Healthy", "checks": {"gateway_tcp": "Healthy"}}
// GET /alive → 200 OK
// GET /metrics → Prometheus 형식 메트릭
app.MapDefaultEndpoints();

// 기존 TCP 서버 시작 (NetMQ)
var gatewayServer = app.Services.GetRequiredService<GatewayTcpServer>();
gatewayServer.Start();

await app.RunAsync();

// Custom Health Check 구현
public class GatewayHealthCheck : IHealthCheck
{
    private readonly GatewayTcpServer _server;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_server.IsRunning)
            return Task.FromResult(HealthCheckResult.Healthy("TCP server is running"));

        return Task.FromResult(HealthCheckResult.Unhealthy("TCP server is stopped"));
    }
}
```

**실행 포트**:
- **TCP 5000**: 게임 클라이언트 연결 (기존)
- **HTTP 5001**: Health Check + Metrics (Aspire 자동 생성)
- **GameSessionChannel 6000**: NetMQ P2P (기존)
- **ServiceMesh 7000**: NetMQ Service Mesh (기존)

### Step 5: GameSample에 Aspire 통합

**GameSample.csproj 수정**:
```xml
<ItemGroup>
  <PackageReference Include="Aspire.Hosting.Redis" />
  <ProjectReference Include="..\Sample.ServiceDefaults\Sample.ServiceDefaults.csproj" />
</ItemGroup>
```

**GameSample/Program.cs 수정** (Host 기반 + Aspire):
```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = Host.CreateDefaultBuilder(args);

// ⭐ Aspire ServiceDefaults for Host-based app
builder.ConfigureHostConfiguration(config =>
{
    config.AddEnvironmentVariables();
});

builder.ConfigureServices((context, services) =>
{
    // ⭐ Aspire ServiceDefaults: OTLP, Metrics, Logging
    services.AddServiceDefaults();

    // ⭐ Redis 연결 문자열 자동 주입
    services.AddRedisClient("redis");

    // GameServer 설정 로드
    var config = context.Configuration.GetSection("GameServer").Get<GameServerConfig>()
        ?? throw new InvalidOperationException("GameServer config not found");

    // GameServer + Node Service Mesh 통합 등록 (기존)
    services.AddGameWithNode(
        gameOptions =>
        {
            gameOptions.GameSessionChannelPort = config.GameSessionChannelPort;
            gameOptions.RedisConnectionString = config.RedisConnectionString;
        },
        nodeGuid: config.NodeGuid,
        serviceMeshPort: config.ServiceMeshPort
    );

    // UserController 및 NodeController 자동 스캔 (기존)
    services.AddUserControllers();
    services.AddNodeControllers();

    // ⭐ Custom Health Check: Actor 시스템 상태
    services.AddHealthChecks()
        .AddCheck<GameServerHealthCheck>("gameserver_actors");

    // ⭐ HTTP 서버 추가 (Health Check용)
    services.AddHostedService<HealthCheckHostedService>();
});

var host = builder.Build();
await host.RunAsync();

// ⭐ Health Check HTTP 서버 (별도 포트, 예: 9102)
public class HealthCheckHostedService : BackgroundService
{
    private readonly IServiceProvider _services;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(_services);
        builder.Services.AddHealthChecks();

        var app = builder.Build();
        app.MapDefaultEndpoints();  // /health, /alive, /metrics

        await app.RunAsync(stoppingToken);
    }
}

// Custom Health Check 구현
public class GameServerHealthCheck : IHealthCheck
{
    private readonly IActorManager _actorManager;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var activeActors = _actorManager.GetActiveCount();

        var data = new Dictionary<string, object>
        {
            { "activeActors", activeActors },
            { "maxActors", 10000 }
        };

        if (activeActors < 10000)
            return Task.FromResult(HealthCheckResult.Healthy($"{activeActors} actors active", data));

        return Task.FromResult(HealthCheckResult.Degraded($"High actor count: {activeActors}", data: data));
    }
}
```

**실행 포트**:
- **GameSessionChannel 9001**: NetMQ P2P (기존)
- **ServiceMesh 9101**: NetMQ Service Mesh (기존)
- **HTTP 9102**: Health Check + Metrics (Aspire 추가) ⭐

### Step 6: AppHost Program.cs 작성

**Sample.AppHost/Program.cs** (TCP + Health Check 포트):
```csharp
var builder = DistributedApplication.CreateBuilder(args);

// 1. Redis (로컬 컨테이너)
var redis = builder.AddRedis("redis", port: 6379)
    .WithDataVolume("redis-data")  // 데이터 영속화
    .WithRedisCommander(port: 8081);  // GUI on http://localhost:8081

// 2. Gateway (단일 인스턴스)
var gateway = builder.AddProject<Projects.GatewaySample>("gateway")
    .WithReference(redis)
    .WithEnvironment("Gateway__TcpPort", "5000")
    .WithEnvironment("Gateway__GameSessionChannelPort", "6000")
    .WithEnvironment("Gateway__ServiceMeshPort", "7000")
    // ⭐ Health Check HTTP 엔드포인트 (별도 포트)
    .WithHttpEndpoint(port: 5001, name: "health")
    .WithHealthCheck();  // Aspire Dashboard에서 상태 확인

// 3. GameServer (단일 인스턴스, 추후 Replica 가능)
var gameServer = builder.AddProject<Projects.GameSample>("gameserver")
    .WithReference(redis)
    .WithEnvironment("GameServer__NodeGuid", Guid.NewGuid().ToString())
    .WithEnvironment("GameServer__GameSessionChannelPort", "9001")
    .WithEnvironment("GameServer__ServiceMeshPort", "9101")
    // ⭐ Health Check HTTP 엔드포인트 (별도 포트)
    .WithHttpEndpoint(port: 9102, name: "health")
    .WithHealthCheck()
    .WithReplicas(1);  // Phase 3에서 증가 가능

// 4. TestClient (선택적)
builder.AddProject<Projects.TestClient>("testclient")
    .WithReference(gateway)
    .WithEnvironment("GatewayAddress", "localhost:5000");

// Dashboard 실행: http://localhost:15001
builder.Build().Run();
```

**Aspire Dashboard에서 확인 가능한 정보**:

### Resources 탭
```
┌─────────────┬──────────┬──────────────┬─────────────────────┐
│ Name        │ State    │ Type         │ Endpoints           │
├─────────────┼──────────┼──────────────┼─────────────────────┤
│ redis       │ Running  │ Container    │ 6379, 8081 (UI)     │
│ gateway     │ Running  │ Project      │ 5001 (health)       │
│ gameserver  │ Running  │ Project      │ 9102 (health)       │
│ testclient  │ Running  │ Project      │ -                   │
└─────────────┴──────────┴──────────────┴─────────────────────┘
```

### Console Logs 탭 (실시간 로그)
```
[gateway] [12:34:56 INF] Gateway started on TCP 5000
[gateway] [12:34:57 INF] GameSessionChannel listening on 6000
[gameserver] [12:34:58 INF] Actor system initialized
[gameserver] [12:34:59 INF] Connected to Redis: localhost:6379
```

### Traces 탭 (분산 추적)
```
Trace: Login Flow (250ms)
├─ gateway: Receive TCP packet (5ms)
├─ gateway: Forward to GameServer (10ms)
├─ gameserver: HandleNewLogin (200ms)
│  ├─ Redis: GetSessionAsync (15ms)
│  ├─ Redis: SetSessionAsync (20ms)
│  └─ Actor: Create SessionActor (50ms)
└─ gateway: Send response (10ms)
```

### Metrics 탭
```
Gateway Metrics:
- CPU: 5%
- Memory: 120 MB
- Active Connections: 150
- Packets/sec: 1,200

GameServer Metrics:
- CPU: 15%
- Memory: 450 MB
- Active Actors: 150
- Redis Ops/sec: 500
```

### Structured Logs 탭
```json
{
  "timestamp": "2026-03-03T12:34:56Z",
  "level": "Information",
  "category": "GameServerHub",
  "message": "New session created",
  "properties": {
    "userId": 12345,
    "sessionId": 67890,
    "gatewayNodeId": 1,
    "traceId": "abc123..."
  }
}
```

### Step 7: appsettings.json 업데이트

**GatewaySample/appsettings.json**:
```json
{
  "Gateway": {
    "TcpPort": 5000,
    "GameSessionChannelPort": 6000,
    "ServiceMeshPort": 7000,
    "RedisConnectionString": "redis"  // ⭐ Aspire가 주입
  }
}
```

**GameSample/appsettings.json**:
```json
{
  "GameServer": {
    "GameSessionChannelPort": 9001,
    "ServiceMeshPort": 9101,
    "RedisConnectionString": "redis"  // ⭐ Aspire가 주입
  }
}
```

---

## 🧪 검증 계획

### 로컬 실행 테스트

```bash
# AppHost 실행
cd Sample/Sample.AppHost
dotnet run

# 예상 결과:
# - Aspire Dashboard: http://localhost:15001
# - Redis Commander: http://localhost:8081
# - Gateway: tcp://localhost:5000
# - GameServer: 자동 시작
```

### Dashboard 확인 항목

1. **Resources 탭**: 모든 서비스 상태 확인 (Running)
2. **Console Logs 탭**: 각 서비스 로그 실시간 확인
3. **Metrics 탭**: CPU, Memory 사용량
4. **Traces 탭**: 분산 추적 (Phase 2)
5. **Redis**: 연결 상태 확인

### 기능 테스트

```bash
# 1. Gateway 연결 테스트
cd Sample/TestClient
dotnet run

# 2. Redis 데이터 확인
# Redis Commander에서 세션 데이터 확인
```

---

## 📊 Phase별 타임라인

| Phase | 작업 내용 | 예상 시간 | 우선순위 |
|-------|-----------|-----------|----------|
| **Phase 0** | Aspire 학습 및 환경 설정 | 1일 | P0 |
| **Phase 1** | 최소 구성 (AppHost + Redis) | 3일 | P0 |
| **Phase 2** | ServiceDefaults + 모니터링 | 5일 | P1 |
| **Phase 3** | 다중 인스턴스 + 배포 | 10일 | P2 |
| **총합** | | **19일** | |

---

## ⚠️ 주의사항 및 제약

### NetMQ와 Aspire의 호환성

1. **P2P 통신**: NetMQ Router-Router 패턴은 Aspire Service Discovery와 독립적
   - **해결**: NetMQ는 IP:Port 직접 바인딩 유지
   - **Aspire 역할**: 환경 변수로 포트 주입

2. **Service Mesh**: NetMQ 클러스터는 Redis P2P Discovery 사용
   - **해결**: Redis를 Aspire로 관리하되, Discovery 로직은 유지
   - **Aspire 역할**: Redis 연결 문자열 자동 주입

3. **포트 고정**: NetMQ는 동적 포트 할당 어려움
   - **해결**: appsettings.json에 고정 포트 유지
   - **Phase 3**: 환경 변수로 동적 할당 시도

### TCP 앱에서 Aspire Observability 활용 ✅

**중요**: TCP 앱도 Aspire ServiceDefaults를 사용하면 완전한 Observability 지원!

1. **OpenTelemetry 자동 구성**
   - **Traces**: 분산 추적 (NetMQ 호출, Redis 액세스)
   - **Metrics**: CPU, Memory, Custom Metrics
   - **Logs**: 구조화된 로그 수집

2. **Health Check HTTP 엔드포인트**
   - TCP 메인 포트와 **별도** HTTP 포트 (예: 5001)
   - `/health`, `/alive` 엔드포인트 자동 생성
   - Aspire Dashboard에서 상태 확인

3. **Dashboard 통합**
   - TCP 앱의 모든 텔레메트리가 Dashboard에 표시
   - 로그, 메트릭, 트레이스 실시간 확인
   - NetMQ 메시지 플로우 추적 가능

### 구현 예시

```csharp
// GatewaySample/Program.cs (TCP 서버)
var builder = WebApplication.CreateBuilder(args);

// ⭐ ServiceDefaults: OTLP, Metrics, Health Check 자동 설정
builder.AddServiceDefaults();

// 기존 Gateway 설정
var config = builder.Configuration.GetSection("Gateway").Get<GatewayConfig>();

// TCP 서버 시작 (NetMQ, 기존 로직)
var gatewayServer = new GatewayTcpServer(config.TcpPort);
gatewayServer.Start();

var app = builder.Build();

// ⭐ Health Check HTTP 엔드포인트 (별도 포트)
// GET /health → {"status": "Healthy", "tcpPort": 5000}
// GET /alive → 200 OK
app.MapDefaultEndpoints();

await app.RunAsync();
```

**결과**:
- **TCP 5000**: 게임 클라이언트 연결 (기존 NetMQ)
- **HTTP 5001**: Health Check 엔드포인트 (Aspire용)
- **Dashboard**: 모든 텔레메트리 실시간 확인

### Aspire의 제한사항

1. **Container Registry**: 로컬 개발 외 배포 시 컨테이너 이미지 필요
   - **Phase 3**: Docker 빌드 자동화

2. **Windows 제약**: Aspire는 Windows에서 Docker Desktop 필요
   - **현재 환경**: Windows 11 Pro 확인됨

---

## 📊 Custom Metrics 수집 예시

### NetMQ 메트릭 추적

**GatewayTcpServer.cs에 Metrics 추가**:
```csharp
using System.Diagnostics.Metrics;

public class GatewayTcpServer
{
    private static readonly Meter _meter = new("Gateway.NetMQ");
    private static readonly Counter<long> _packetsReceived =
        _meter.CreateCounter<long>("packets_received", "packets", "Total packets received from clients");
    private static readonly Counter<long> _packetsForwarded =
        _meter.CreateCounter<long>("packets_forwarded", "packets", "Total packets forwarded to GameServer");
    private static readonly Histogram<double> _packetSize =
        _meter.CreateHistogram<double>("packet_size_bytes", "bytes", "Size of received packets");

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        // ⭐ 메트릭 기록
        _packetsReceived.Add(1, new KeyValuePair<string, object?>("endpoint", "tcp"));
        _packetSize.Record(size);

        // 기존 로직...
        _packetRouter.ForwardToGameServer(SocketId, packetSpan, targetNodeId, sessionId);

        _packetsForwarded.Add(1);
    }
}
```

**GameServerHub.cs에 Actor 메트릭 추가**:
```csharp
using System.Diagnostics.Metrics;

public class GameServerHub
{
    private static readonly Meter _meter = new("GameServer.Actors");
    private static readonly Counter<long> _actorsCreated =
        _meter.CreateCounter<long>("actors_created", "actors", "Total actors created");
    private static readonly Counter<long> _actorsDestroyed =
        _meter.CreateCounter<long>("actors_destroyed", "actors", "Total actors destroyed");
    private static readonly ObservableGauge<int> _activeActors =
        _meter.CreateObservableGauge("actors_active", () => _actorManager.GetActiveCount());

    private async Task HandleNewLogin(long userId, PacketContext context)
    {
        // Actor 생성
        CreateActorForSession(newSessionId, userId, context.GatewayNodeId, context.SocketId);

        // ⭐ 메트릭 기록
        _actorsCreated.Add(1,
            new KeyValuePair<string, object?>("userId", userId),
            new KeyValuePair<string, object?>("nodeId", _config.NodeId));
    }
}
```

**Dashboard에서 확인**:
```
Gateway Metrics:
- gateway.netmq.packets_received: 1,200/sec
- gateway.netmq.packets_forwarded: 1,150/sec
- gateway.netmq.packet_size_bytes (P50): 256 bytes
- gateway.netmq.packet_size_bytes (P99): 4,096 bytes

GameServer Metrics:
- gameserver.actors.actors_created: 50 (total)
- gameserver.actors.actors_destroyed: 10 (total)
- gameserver.actors.actors_active: 40 (current)
```

---

## 🔗 참고 자료

- [.NET Aspire 공식 문서](https://learn.microsoft.com/en-us/dotnet/aspire/)
- [Aspire Redis Component](https://learn.microsoft.com/en-us/dotnet/aspire/caching/stackexchange-redis-component)
- [Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard)
- [OpenTelemetry .NET Metrics](https://opentelemetry.io/docs/languages/net/metrics/)
- [System.Diagnostics.Metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics)
- [NetMQ + Aspire 통합 예제](https://github.com/dotnet/aspire-samples)

---

## 📋 체크리스트

### Phase 0: 사전 준비

- [ ] Docker Desktop 설치 확인
- [ ] .NET Aspire Workload 설치
- [ ] Redis Commander 확인
- [ ] 기존 프로젝트 백업

### Phase 1: 최소 구성

- [ ] Sample.AppHost 프로젝트 생성
- [ ] Redis 컨테이너 추가
- [ ] GatewaySample Aspire 통합
- [ ] GameSample Aspire 통합
- [ ] Dashboard 실행 및 검증
- [ ] TestClient 연결 테스트

### Phase 2: 개발 환경 개선

- [ ] Sample.ServiceDefaults 생성
- [ ] Health Check 추가
- [ ] OpenTelemetry 통합
- [ ] 환경별 설정 분리

### Phase 3: 고급 기능

- [ ] GameServer Replica 설정
- [ ] 배포 매니페스트 생성
- [ ] CI/CD 파이프라인 통합

---

## 🚀 다음 단계

1. **Phase 0 완료 후**: Aspire 기본 구성 검증
2. **Phase 1 완료 후**: Actor 생성 프로세스 버그 수정 재개
3. **Phase 2 완료 후**: 성능 테스트 및 모니터링
4. **Phase 3 완료 후**: 프로덕션 배포 준비

---

**작성자**: Claude Sonnet 4.5
**검토 필요**: Aspire와 NetMQ의 호환성 실험
