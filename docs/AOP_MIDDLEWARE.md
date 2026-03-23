# AOP Middleware Pattern 구현

## 개요

GameServer에 ASP.NET Core 스타일의 Middleware Pattern 기반 AOP (Aspect-Oriented Programming)를 구현했습니다.

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

**목적**: 전역 예외 처리 및 에러 응답 생성

**기능**:
- 모든 예외를 캐치하여 로깅
- 에러 응답 자동 생성
- 클라이언트에게 에러 메시지 전송

**순서**: 1번 (최상위 - 모든 예외를 캐치해야 함)

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

**목적**: 패킷 수신/처리 로깅 (AOP - Cross-cutting Concern)

**기능**:
- Request 로깅 (Gateway, Socket, Session, Size)
- Response 로깅 (ResponseSize)
- 예외 발생 시 로깅

**순서**: 2번 (예외 처리 다음)

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

**목적**: 성능 측정 및 Slow Packet 감지

**기능**:
- Stopwatch로 처리 시간 측정
- 100ms 이상 걸리면 경고 로그
- Context.Items에 실행 시간 저장

**순서**: 3번 (비즈니스 로직 직전)

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

**목적**: 실제 비즈니스 로직 실행 (GameServerHub 위임)

**기능**:
- IPacketProcessor.ProcessAsync() 호출
- GameServerHub의 실제 패킷 처리 로직 실행

**순서**: 4번 (마지막 - 실제 비즈니스 로직)

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

## 이점

### 1. 관심사 분리 (Separation of Concerns)
- 비즈니스 로직과 횡단 관심사 분리
- 각 Middleware가 단일 책임 원칙 준수

### 2. 재사용성 (Reusability)
- Middleware를 다른 패킷 처리 파이프라인에서 재사용 가능
- 독립적으로 테스트 가능

### 3. 유연성 (Flexibility)
- Middleware 추가/제거가 쉬움
- 실행 순서 변경 가능
- 조건부 Middleware 실행 가능

### 4. 테스트 용이성 (Testability)
- 각 Middleware를 독립적으로 단위 테스트
- Mock을 사용한 파이프라인 테스트 가능

### 5. 유지보수성 (Maintainability)
- 명확한 코드 구조
- 새로운 기능 추가가 기존 코드에 영향 없음

## 향후 확장 계획

### 추가 가능한 Middleware

1. **ValidationMiddleware**: 패킷 유효성 검사
2. **CachingMiddleware**: 응답 캐싱
3. **RateLimitingMiddleware**: 요청 속도 제한
4. **CompressionMiddleware**: 패킷 압축/해제
5. **EncryptionMiddleware**: 패킷 암호화/복호화
6. **MetricsMiddleware**: Prometheus/StatsD 메트릭 수집
7. **TracingMiddleware**: 분산 추적 (OpenTelemetry)

### 조건부 Middleware

특정 Opcode에만 적용되는 Middleware:

```csharp
public class ConditionalMiddleware : IPacketMiddleware
{
    public async Task InvokeAsync(PacketContext context, Func<Task> next)
    {
        if (context.Opcode == 100) // 특정 Opcode만
        {
            // 특별 처리
        }

        await next();
    }
}
```

## 결론

ASP.NET Core의 Middleware Pattern을 GameServer 패킷 처리에 적용하여:
- **AOP 구현**: 횡단 관심사를 선언적으로 처리
- **확장 가능**: 새로운 기능을 쉽게 추가
- **유지보수 용이**: 명확한 코드 구조
- **테스트 가능**: 각 Middleware 독립적으로 테스트

이를 통해 GameServer의 코드 품질과 유지보수성을 크게 향상시켰습니다.
