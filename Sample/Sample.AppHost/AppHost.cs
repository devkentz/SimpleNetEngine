
// UTF-8 콘솔 인코딩 설정 (한글 깨짐 방지)
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;




var builder = DistributedApplication.CreateBuilder(args);

#pragma warning disable ASPIRECERTIFICATES001
// 2. Redis (개발 전용: TLS/비밀번호 없음, 포트 6379 고정)
var redis = builder.AddRedis("redis")
    .WithOtlpExporter()
    .WithoutHttpsCertificate()
    .WithPassword(null)
    .WithEndpoint("tcp", e => e.Port = 6379);
#pragma warning restore ASPIRECERTIFICATES001


// 3. Gateway (TCP 서버 + Health Check HTTP)
var gateway = builder.AddProject<Projects.GatewaySample>("gateway")
    .WithReference(redis)
    .WaitFor(redis)
    .WithHttpEndpoint(name: "health")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    // Aspire 동적 포트 → Gateway__XXX 환경변수 → GatewayConfig에 자동 바인딩
    .WithEndpoint(name: "service-mesh", scheme: "tcp", env: "Gateway__ServiceMeshPort")
    .WithEndpoint(name: "client-tcp", scheme: "tcp", env: "Gateway__TcpPort", port: 5000)
    .WithReplicas(2);

// 4. GameServer (다중 인스턴스 대응)
var gameServer = builder.AddProject<Projects.GameSample>("gameserver")
    .WithReference(redis)
    .WaitFor(redis)
    .WithHttpEndpoint(name: "health")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    // Aspire 동적 포트 → GameServer__XXX 환경변수 → GameOptions에 자동 바인딩
    .WithEndpoint(name: "user-mesh", scheme: "tcp", env: "GameServer__GameSessionChannelPort")
    .WithEndpoint(name: "service-mesh", scheme: "tcp", env: "GameServer__ServiceMeshPort")
    .WithReplicas(2);

// 5. NodeSample (Stateless Service — Parallel 기반)
var nodeSample = builder.AddProject<Projects.NodeSample>("nodesample")
    .WithReference(redis)
    .WaitFor(redis)
    .WithHttpEndpoint(name: "health")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEndpoint(name: "service-mesh", scheme: "tcp", env: "NodeSample__ServiceMeshPort")
    .WithReplicas(3);

// Aspire Dashboard 실행
await builder.Build().RunAsync();
 
