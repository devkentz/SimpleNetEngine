
// UTF-8 콘솔 인코딩 설정 (한글 깨짐 방지)
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

var builder = DistributedApplication.CreateBuilder(args);

// OTLP endpoint (appsettings.json → OtlpEndpoint)
// Aspire DCP가 OTEL_EXPORTER_OTLP_ENDPOINT를 자체 Dashboard로 덮어쓰므로
// DOTNET_DASHBOARD_OTLP_ENDPOINT_URL을 비활성화하고 직접 주입
var otlpEndpoint = builder.Configuration["OtlpEndpoint"];

// 1. Redis — 외부 인스턴스 연결 (appsettings.json → ConnectionStrings:redis)
//    Aspire가 컨테이너를 생성하지 않고, 설정된 연결 문자열을 하위 프로젝트에 주입
var redis = builder.AddConnectionString("redis");

// 2. Gateway (TCP 서버 + Health Check HTTP)
var gateway = builder.AddProject<Projects.GatewaySample>("gateway")
    .WithReference(redis)
    .WithHttpEndpoint(name: "health")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint!)
    // Aspire 동적 포트 → Gateway__XXX 환경변수 → GatewayConfig에 자동 바인딩
    .WithEndpoint(name: "service-mesh", scheme: "tcp", env: "Gateway__ServiceMeshPort")
    .WithEndpoint(name: "client-tcp", scheme: "tcp", env: "Gateway__TcpPort", port: 5000)
    .WithReplicas(2);

// 3. GameServer (다중 인스턴스 대응)
var gameServer = builder.AddProject<Projects.GameSample>("gameserver")
    .WithReference(redis)
    .WithHttpEndpoint(name: "health")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint!)
    // Aspire 동적 포트 → GameServer__XXX 환경변수 → GameOptions에 자동 바인딩
    .WithEndpoint(name: "user-mesh", scheme: "tcp", env: "GameServer__GameSessionChannelPort")
    .WithEndpoint(name: "service-mesh", scheme: "tcp", env: "GameServer__ServiceMeshPort")
    .WithReplicas(1);

// 4. NodeSample (Stateless Service — Parallel 기반)
var nodeSample = builder.AddProject<Projects.NodeSample>("nodesample")
    .WithReference(redis)
    .WithHttpEndpoint(name: "health")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint!)
    .WithEndpoint(name: "service-mesh", scheme: "tcp", env: "NodeSample__ServiceMeshPort")
    .WithReplicas(1);

// Aspire Dashboard 실행
await builder.Build().RunAsync();
