
// UTF-8 콘솔 인코딩 설정 (한글 깨짐 방지)
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

var builder = DistributedApplication.CreateBuilder(args);

// 1. Redis — Aspire Dev Container (Docker로 자동 생성, Dashboard에서 모니터링 가능)
var redis = builder.AddRedis("redis");

// 2. Gateway (TCP 서버 + Health Check HTTP)
var gateway = builder.AddProject<Projects.GatewaySample>("gateway")
    .WithReference(redis).WaitFor(redis)
    .WithHttpEndpoint(name: "health")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    // Aspire 동적 포트 → Gateway__XXX 환경변수 → GatewayConfig에 자동 바인딩
    .WithEndpoint(name: "service-mesh", scheme: "tcp", env: "Gateway__ServiceMeshPort")
    .WithEndpoint(name: "client-tcp", scheme: "tcp", env: "Gateway__TcpPort", port: 5000)
    .WithReplicas(2);

// 3. GameServer (다중 인스턴스 대응)
var gameServer = builder.AddProject<Projects.GameSample>("gameserver")
    .WithReference(redis).WaitFor(redis)
    .WithHttpEndpoint(name: "health")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    // Aspire 동적 포트 → GameServer__XXX 환경변수 → GameOptions에 자동 바인딩
    .WithEndpoint(name: "user-mesh", scheme: "tcp", env: "GameServer__GameSessionChannelPort")
    .WithEndpoint(name: "service-mesh", scheme: "tcp", env: "GameServer__ServiceMeshPort")
    .WithReplicas(1);

// 4. NodeSample (Stateless Service — Parallel 기반)
var nodeSample = builder.AddProject<Projects.NodeSample>("nodesample")
    .WithReference(redis).WaitFor(redis)
    .WithHttpEndpoint(name: "health")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEndpoint(name: "service-mesh", scheme: "tcp", env: "NodeSample__ServiceMeshPort")
    .WithReplicas(1);

// Aspire Dashboard 실행
await builder.Build().RunAsync();
