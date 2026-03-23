# AOP Middleware Pattern 구현

## 개요

GameServer 패킷 처리에 ASP.NET Core 스타일의 Middleware Pattern을 적용한 구조.

## 아키텍처

```
Client Packet → GatewayPacketListener → GameServerHub
                                        ↓
                                  [Middleware Pipeline]
                                        ↓
                    ┌───────────────────┴────────────────────┐
                    │                                        │
            ExceptionHandlingMiddleware                      │
                    │                                        │
              LoggingMiddleware                             │
                    │                                        │
            PerformanceMiddleware                           │
                    │                                        │
           PacketHandlerMiddleware                          │
                    │                                        │
                    └──────────► Business Logic ────────────┘
                                        ↓
                                   Response
```

## 핵심 컴포넌트

### 1. IPacketMiddleware 인터페이스

```csharp
public interface IPacketMiddleware
{
    Task InvokeAsync(PacketContext context, Func<Task> next);
}
```

- **InvokeAsync**: Middleware 실행 메서드
- **context**: 패킷 처리 컨텍스트 (Request/Response 데이터)
- **next**: 다음 Middleware 호출 delegate

### 2. PacketContext

패킷 처리 중 공유되는 컨텍스트 객체:

```csharp
public class PacketContext
{
    // Request 정보
    public long GatewayNodeId { get; set; }
    public Guid SocketId { get; set; }
    public long SessionId { get; set; }
    public byte[] Payload { get; set; }

    // Response 정보
    public byte[]? Response { get; set; }
    public Action<long, Guid, long, byte[]>? SendResponse { get; set; }

    // 에러 처리
    public Exception? Exception { get; set; }

    // Middleware 간 데이터 공유
    public Dictionary<string, object> Items { get; }

    // 메타데이터
    public DateTime StartTime { get; set; }
    public bool IsCompleted { get; set; }
    public long? UserId { get; set; }
    public int? Opcode { get; set; }
}
```

### 3. MiddlewarePipeline

Middleware 체인을 관리하고 순차적으로 실행:

```csharp
public class MiddlewarePipeline
{
    public MiddlewarePipeline Use(IPacketMiddleware middleware);
    public Task ExecuteAsync(PacketContext context);
}
```

### 4. MiddlewarePipelineFactory

DI를 통해 Middleware 인스턴스를 생성하고 Pipeline 구성:

```csharp
public MiddlewarePipeline CreateDefaultPipeline()
{
    var pipeline = new MiddlewarePipeline(_pipelineLogger);

    pipeline.Use(GetMiddleware<ExceptionHandlingMiddleware>());
    pipeline.Use(GetMiddleware<LoggingMiddleware>());
    pipeline.Use(GetMiddleware<PerformanceMiddleware>());
    pipeline.Use(GetMiddleware<PacketHandlerMiddleware>());

    return pipeline;
}
```

## 기본 Middleware 구현

### 1. ExceptionHandlingMiddleware

파이프라인 최상위에서 모든 예외를 캐치하여 로깅하고 에러 응답을 생성한다.

```csharp
public async Task InvokeAsync(PacketContext context, Func<Task> next)
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Unhandled exception...");
        context.Exception = ex;
        context.Response = CreateErrorResponse(ex);
        // 에러 응답 전송
    }
}
```

### 2. LoggingMiddleware

예외 처리 다음 단계에서 Request/Response를 로깅한다. Gateway, Socket, Session, Size 등의 정보를 기록.

```csharp
public async Task InvokeAsync(PacketContext context, Func<Task> next)
{
    _logger.LogDebug("Packet received: Socket={SocketId}...");

    await next();

    if (context.Response != null)
    {
        _logger.LogDebug("Packet response sent...");
    }
}
```

### 3. PerformanceMiddleware

비즈니스 로직 직전에서 Stopwatch로 처리 시간을 측정하고, 100ms 이상이면 경고 로그를 남긴다.

```csharp
public async Task InvokeAsync(PacketContext context, Func<Task> next)
{
    var stopwatch = Stopwatch.StartNew();

    await next();

    stopwatch.Stop();
    var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;

    if (elapsedMs > SlowPacketThresholdMs)
    {
        _logger.LogWarning("Slow packet detected: {ElapsedMs}ms", elapsedMs);
    }
}
```

### 4. PacketHandlerMiddleware

파이프라인 마지막 단계. IPacketProcessor.ProcessAsync()를 호출하여 실제 비즈니스 로직을 실행한다.

```csharp
public async Task InvokeAsync(PacketContext context, Func<Task> next)
{
    await _packetProcessor.ProcessAsync(context);
    await next();
}
```

## GameServerHub 통합

### IClientPacketHandler 구현

기존 인터페이스 유지하면서 내부적으로 Middleware Pipeline 사용:

```csharp
public async Task HandlePacketAsync(
    ClientPacketContext context,
    Action<long, Guid, long, byte[]> sendResponse)
{
    // ClientPacketContext → PacketContext 변환
    var packetContext = new PacketContext { ... };

    // Middleware Pipeline 실행
    await _pipeline.ExecuteAsync(packetContext);

    // 응답 전송
    if (packetContext.Response != null && !packetContext.IsCompleted)
    {
        sendResponse(...);
    }
}
```

### IPacketProcessor 구현

실제 비즈니스 로직 처리:

```csharp
public async Task ProcessAsync(PacketContext context)
{
    if (context.SessionId == 0)
    {
        await HandleUnpinnedSession(context);
    }
    else
    {
        await HandlePinnedSession(context);
    }
}
```

## DI 구성

```csharp
// Middleware Pipeline (AOP)
services.AddSingleton<MiddlewarePipelineFactory>();
services.AddSingleton<ExceptionHandlingMiddleware>();
services.AddSingleton<LoggingMiddleware>();
services.AddSingleton<PerformanceMiddleware>();
services.AddSingleton<PacketHandlerMiddleware>();

// GameServerHub (IPacketProcessor + IClientPacketHandler)
services.AddSingleton<GameServerHub>();
services.AddSingleton<IPacketProcessor>(sp => sp.GetRequiredService<GameServerHub>());
services.AddSingleton<IClientPacketHandler>(sp => sp.GetRequiredService<GameServerHub>());
```

## 확장 가능한 설계

### 새로운 Middleware 추가 방법

1. **IPacketMiddleware 구현**:

```csharp
public class AuthorizationMiddleware : IPacketMiddleware
{
    public async Task InvokeAsync(PacketContext context, Func<Task> next)
    {
        // 권한 검증 로직
        if (!IsAuthorized(context))
        {
            context.Response = CreateUnauthorizedResponse();
            return; // next() 호출 안 함 = 파이프라인 중단
        }

        await next();
    }
}
```

2. **DI 등록**:

```csharp
services.AddSingleton<AuthorizationMiddleware>();
```

3. **Pipeline에 추가**:

```csharp
public MiddlewarePipeline CreateDefaultPipeline()
{
    var pipeline = new MiddlewarePipeline(_pipelineLogger);

    pipeline.Use(GetMiddleware<ExceptionHandlingMiddleware>());
    pipeline.Use(GetMiddleware<LoggingMiddleware>());
    pipeline.Use(GetMiddleware<AuthorizationMiddleware>()); // 추가!
    pipeline.Use(GetMiddleware<PerformanceMiddleware>());
    pipeline.Use(GetMiddleware<PacketHandlerMiddleware>());

    return pipeline;
}
```
