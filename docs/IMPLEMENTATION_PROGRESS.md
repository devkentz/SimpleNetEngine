# Implementation Progress

## Priority 1: Gateway Session-Based Routing ✅ COMPLETED

### 목표
Gateway에서 Singleton SessionMapper 대신 각 GatewaySession 객체가 자신의 라우팅 정보를 메모리에 저장하도록 변경

### 구현 내용

#### 1. GatewaySession.cs 수정
- **새 필드 추가**:
  - `PinnedGameServerNodeId`: 고정된 GameServer NodeId (0 = 미고정)
  - `GameSessionId`: 게임 세션 ID (0 = 미할당)
  - `_lock`: 스레드 안전성을 위한 lock 객체

- **새 메서드 추가**:
  - `Pin(gameServerNodeId, gameSessionId)`: 세션을 특정 GameServer에 고정
  - `Reroute(newTargetNodeId)`: 다른 GameServer로 재라우팅
  - `Unpin()`: 세션 고정 해제

- **OnReceived 수정**:
  - 세션 객체에서 직접 `PinnedGameServerNodeId`와 `GameSessionId`를 읽어서 전달
  - Lock-Free 읽기로 성능 최적화

- **OnDisconnected 수정**:
  - 연결 종료 시 `Unpin()` 호출하여 세션 정보 정리

#### 2. GamePacketRouter.cs 수정
- **ForwardToGameServer 시그니처 변경**:
  ```csharp
  // 이전: ForwardToGameServer(Guid socketId, byte[] clientData)
  // 이후: ForwardToGameServer(Guid socketId, byte[] clientData, long pinnedNodeId, long sessionId)
  ```
  - 세션 객체에서 라우팅 정보를 받아서 처리
  - `pinnedNodeId > 0`이면 해당 GameServer로 직접 전송
  - `pinnedNodeId == 0`이면 라운드 로빈으로 선택

- **HandleControlPacket 개선**:
  - `PinSession`: 세션 객체의 `Pin()` 메서드 호출 (SessionMapper는 하위 호환용으로만 유지)
  - `DisconnectClient`: 기존 로직 유지
  - `RerouteSocket`: 새로 추가된 제어 명령 처리

#### 3. P2PProtocol.cs 업데이트
- **ControlCommand enum에 추가**:
  - `RerouteSocket = 3`: 재라우팅 제어 명령 (재접속, 중복 로그인 처리용)

#### 4. 기타 수정
- **GameServerConnector.cs 제거**: 더 이상 사용되지 않는 레거시 코드 삭제
- **Program.cs 수정**: ILoggerFactory 주입으로 GatewayTcpServer용 Logger 생성

### 아키텍처 개선사항

#### Lock-Free Session-Based Routing
- 각 GatewaySession이 자신의 라우팅 정보를 메모리에 저장
- 읽기 작업은 Lock-Free (성능 향상)
- 쓰기 작업만 Lock 사용 (PinSession, Reroute 등)

#### 이점
1. **성능**: Singleton Dictionary 조회 없이 세션 객체에서 직접 읽기
2. **확장성**: 세션별로 독립적인 메모리 관리
3. **단순성**: 중앙 집중식 SessionMapper 의존성 감소
4. **스레드 안전**: Lock을 세션 단위로 사용하여 경합 최소화

### 빌드 검증
```bash
✅ GatewayServer.csproj - Build SUCCESS (0 warnings, 0 errors)
✅ GameServer.csproj - Build SUCCESS (0 warnings, 0 errors)
```

### 다음 단계
Priority 3: Service Mesh Kick-out Protocol 구현 시작

---

## Priority 2: GameServer Redis Session Management ✅ COMPLETED

### 목표
- ISessionStore 인터페이스 정의
- Redis 연동 (StackExchange.Redis)
- 중복 로그인 감지 및 처리

### 구현 내용

#### 1. ISessionStore 인터페이스 (GameServer/Session/ISessionStore.cs)
- **SessionInfo record**: Redis에 저장될 세션 정보 구조
  - GameServerNodeId, SessionId, GatewayNodeId, SocketId
  - CreatedAtUtc, LastActivityUtc

- **인터페이스 메서드**:
  - `GetSessionAsync(userId)`: 세션 정보 조회
  - `SetSessionAsync(userId, sessionInfo, ttl)`: 세션 저장/갱신
  - `DeleteSessionAsync(userId)`: 세션 삭제
  - `IsSessionOnNodeAsync(userId, nodeId)`: 특정 노드 세션 확인

#### 2. RedisSessionStore 구현 (GameServer/Session/RedisSessionStore.cs)
- **StackExchange.Redis 활용**: IConnectionMultiplexer 기반
- **Key 형식**: `session:{userId}`
- **기본 TTL**: 24시간
- **JSON 직렬화**: System.Text.Json 사용
- **에러 처리**: 모든 Redis 작업에 try-catch 및 로깅

#### 3. GameServerHub 업데이트 (GameServer/Core/GameServerHub.cs)
- **중복 로그인 감지**:
  ```csharp
  var existingSession = await _sessionStore.GetSessionAsync(userId);
  if (existingSession != null) {
      await HandleDuplicateLogin(...);
  }
  ```

- **HandleDuplicateLogin 메서드**:
  - 같은 GameServer: `SendDisconnectClient()` 직접 호출
  - 다른 GameServer: Service Mesh 통신 필요 (Priority 3에서 구현)
  - 기존 세션 정리 후 새 세션 생성
  - Redis 세션 정보 갱신

- **신규 로그인**:
  - SessionInfo 생성
  - Redis에 저장 (SSOT)
  - Gateway에게 PinSession 지시
  - 클라이언트에게 로그인 성공 응답

#### 4. GatewayPacketListener 업데이트
- **SendDisconnectClient 메서드 추가**:
  - DisconnectClient 제어 패킷 전송
  - 중복 로그인 시 기존 소켓 강제 종료용

#### 5. Program.cs DI 구성
- **IConnectionMultiplexer 등록**: Redis 연결
- **ISessionStore 등록**: RedisSessionStore 구현체

### 아키텍처 특징

#### SSOT (Single Source Of Truth)
- Redis를 유일한 세션 정보 저장소로 사용
- 모든 GameServer가 Redis를 통해 세션 상태 동기화

#### 중복 로그인 처리 전략
1. **Same-Node Duplicate**: 직접 P2P DisconnectClient 전송
2. **Cross-Node Duplicate**: Service Mesh KickoutRequest 필요 (Priority 3)
3. **Session Cleanup**: 기존 세션 제거 후 새 세션 생성

#### TTL 관리
- 기본 24시간 세션 만료
- 활동 시 LastActivityUtc 갱신 (추후 구현)

### 빌드 검증
```bash
✅ GameServer.csproj - Build SUCCESS (0 warnings, 0 errors)
```

### 다음 단계
Priority 4: Reconnect Handling 구현 시작

---

## Priority 3: Service Mesh Kick-out Protocol ✅ COMPLETED

### 목표
- 다른 GameServer에 로그인한 중복 세션 kick-out
- Service Mesh 통신 (GameServer ↔ GameServer)
- 동기식 대기 (KickoutAck 응답 받을 때까지)

### 구현 내용

#### 1. ServiceMeshProtocol 정의 (SimpleNetEngine.Common/Packets/ServiceMeshProtocol.cs)
- **Header 구조** (25 bytes):
  - MessageType(1) + SourceNodeId(8) + RequestId(8) + PayloadSize(4) + Reserved(4)

- **MessageType enum**:
  - `KickoutRequest`: GameServer → GameServer kick-out 요청
  - `KickoutAck`: kick-out 완료 응답
  - `ServiceRequest/Response`: Stateless Service 통신용 (향후 사용)

- **KickoutRequest Payload** (40 bytes):
  - UserId, SessionId, GatewayNodeId, SocketId

- **KickoutAck Payload** (12 bytes):
  - UserId, Success, ErrorCode

- **KickoutErrorCode enum**:
  - Success, UserNotFound, SessionMismatch, AlreadyDisconnected, InternalError

- **PacketBuilder**:
  - `CreateKickoutRequest()`: RequestId 자동 생성
  - `CreateKickoutAck()`: 응답 패킷 생성

#### 2. NodeCommunicator 구현 (GameServer/Network/NodeCommunicator.cs)
- **NetMQ DealerSocket**: Service Mesh 통신
- **Identity**: `GameServer-{NodeId}`
- **동기식 Kick-out 요청**:
  ```csharp
  public async Task<KickoutAck> SendKickoutRequestAsync(
      long targetNodeId, long userId, long sessionId, ...)
  {
      // RequestId 생성 및 TaskCompletionSource 등록
      // 패킷 전송
      // 타임아웃 5초와 함께 응답 대기
  }
  ```

- **Pending Request 관리**:
  - `ConcurrentDictionary<RequestId, TaskCompletionSource<KickoutAck>>`
  - 타임아웃: 5초
  - 응답 수신 시 TaskCompletionSource.SetResult()

- **KickoutRequest 처리**:
  - 다른 GameServer로부터 요청 수신
  - 로컬 세션 확인 및 disconnect
  - KickoutAck 응답 전송

#### 3. GameServerHub 업데이트
- **HandleDuplicateLogin 개선**:
  ```csharp
  if (existingSession.GameServerNodeId != _config.NodeId) {
      // Cross-Node: Service Mesh KickoutRequest
      var ack = await _nodeCommunicator.SendKickoutRequestAsync(...);
      kickoutSuccess = ack.Success;
  } else {
      // Same-Node: 직접 DisconnectClient
      _p2pListener.SendDisconnectClient(...);
      kickoutSuccess = true;
  }
  ```

- **Kick-out 실패 처리**:
  - 실패하더라도 새 세션 생성 허용
  - 로깅으로 문제 추적

#### 4. Program.cs DI 구성
- **NodeCommunicator 등록**: Singleton
- **GameServerHostedService 수정**:
  - NodeCommunicator.Start() 호출
  - Service Mesh 준비 로그 출력

### 아키텍처 특징

#### Network Dualism 준수
- **P2P Channel**: Gateway ↔ GameServer (클라이언트 패킷)
- **Service Mesh Channel**: GameServer ↔ GameServer (제어 메시지)
- 두 채널은 완전히 분리됨

#### 동기식 Request-Response 패턴
- RequestId로 요청-응답 매칭
- TaskCompletionSource로 비동기 대기
- 타임아웃으로 무한 대기 방지

#### Kick-out 시나리오
1. **Cross-Node Duplicate**:
   - NodeCommunicator.SendKickoutRequestAsync() 호출
   - 대상 GameServer에서 DisconnectClient 전송
   - KickoutAck 응답 대기 (최대 5초)

2. **Same-Node Duplicate**:
   - GatewayPacketListener.SendDisconnectClient() 직접 호출
   - 로컬 처리로 빠른 응답

### 빌드 검증
```bash
✅ GameServer.csproj - Build SUCCESS (0 warnings, 0 errors)
```

### 다음 단계
Priority 4: Reconnect Handling (Re-pinning, Snapshot Sync)

---

## AOP Middleware Pattern ✅ COMPLETED

### 목표
- GameServer 패킷 처리에 AOP 적용
- Middleware Pattern으로 횡단 관심사 분리
- 확장 가능하고 테스트 가능한 구조

### 구현 내용

#### 1. 핵심 인터페이스 및 컨텍스트
- **IPacketMiddleware**: Middleware 인터페이스
  ```csharp
  Task InvokeAsync(PacketContext context, Func<Task> next);
  ```

- **PacketContext**: 패킷 처리 컨텍스트
  - Request: GatewayNodeId, SocketId, SessionId, Payload
  - Response: Response, SendResponse
  - 메타데이터: UserId, Opcode, StartTime, Items
  - 에러: Exception, IsCompleted

#### 2. Middleware Pipeline
- **MiddlewarePipeline**: Middleware 체인 관리 및 실행
- **MiddlewarePipelineFactory**: DI 기반 Pipeline 생성

#### 3. 기본 Middleware 구현

**ExceptionHandlingMiddleware** (최상위):
- 전역 예외 처리 및 로깅
- 에러 응답 자동 생성
- 클라이언트에게 에러 전송

**LoggingMiddleware**:
- Request/Response 로깅
- AOP Cross-cutting Concern
- 예외 발생 시 로깅

**PerformanceMiddleware**:
- Stopwatch로 처리 시간 측정
- Slow Packet 감지 (100ms 이상 경고)
- Context.Items에 실행 시간 저장

**PacketHandlerMiddleware** (마지막):
- IPacketProcessor.ProcessAsync() 호출
- 실제 비즈니스 로직 실행

#### 4. GameServerHub 통합
- **IClientPacketHandler 유지**: 기존 인터페이스 호환
- **IPacketProcessor 구현**: 비즈니스 로직 처리
- **ClientPacketContext → PacketContext 변환**
- **Middleware Pipeline 실행**

#### 5. DI 구성
```csharp
services.AddSingleton<MiddlewarePipelineFactory>();
services.AddSingleton<ExceptionHandlingMiddleware>();
services.AddSingleton<LoggingMiddleware>();
services.AddSingleton<PerformanceMiddleware>();
services.AddSingleton<PacketHandlerMiddleware>();

services.AddSingleton<GameServerHub>();
services.AddSingleton<IPacketProcessor>(sp => sp.GetRequiredService<GameServerHub>());
services.AddSingleton<IClientPacketHandler>(sp => sp.GetRequiredService<GameServerHub>());
```

### 아키텍처 특징

#### Middleware 실행 순서
```
Client Packet
    ↓
ExceptionHandlingMiddleware (예외 처리)
    ↓
LoggingMiddleware (로깅)
    ↓
PerformanceMiddleware (성능 측정)
    ↓
PacketHandlerMiddleware (비즈니스 로직)
    ↓
Response
```

#### AOP 이점
1. **관심사 분리**: 비즈니스 로직과 횡단 관심사 분리
2. **재사용성**: Middleware를 독립적으로 재사용 가능
3. **유연성**: Middleware 추가/제거/순서 변경 용이
4. **테스트 용이성**: 각 Middleware 독립적으로 단위 테스트
5. **유지보수성**: 명확한 코드 구조

#### 확장 가능 설계
- 새로운 Middleware 추가 시 기존 코드 수정 불필요
- 조건부 Middleware 실행 가능 (특정 Opcode만)
- DI를 통한 의존성 주입

### 향후 확장 계획
- ValidationMiddleware (패킷 유효성 검사)
- CachingMiddleware (응답 캐싱)
- RateLimitingMiddleware (요청 속도 제한)
- CompressionMiddleware (패킷 압축)
- MetricsMiddleware (Prometheus 메트릭 수집)
- TracingMiddleware (분산 추적)

### 빌드 검증
```bash
✅ GameServer.csproj - Build SUCCESS (0 warnings, 0 errors)
```

### 문서
상세 내용은 [AOP_MIDDLEWARE.md](./AOP_MIDDLEWARE.md) 참조

---

## Priority 4: Reconnect Handling ✅ COMPLETED

### 목표
- 재접속 시나리오 처리
- Re-pinning 메커니즘 (동일 GameServer)
- Reroute 메커니즘 (다른 GameServer)
- Snapshot 동기화 (현재 상태만)

### 구현 내용

#### 1. ISessionSnapshot 인터페이스
- **SnapshotData**: UserState, GameState, LastSequenceId, Version
- **ISessionSnapshot**: CreateSnapshotAsync, ApplySnapshotAsync
- **InMemorySessionSnapshot**: 임시 인메모리 구현

#### 2. GameServerHub 재접속 처리
- HandleUnpinnedSession: 신규 로그인 vs 재접속 구분
- HandleNewLogin: 신규 로그인 로직 분리
- HandleReconnect: Same-Node / Cross-Node 분기
- ExtractLoginInfo: UserId, isReconnect, oldSessionId 추출

#### 3. Same-Node Reconnect (Re-pinning)
- 기존 SessionId 재사용
- 세션 정보 업데이트 (새 Gateway, 새 SocketId)
- Re-pinning 지시
- 스냅샷 생성 및 전송

#### 4. Cross-Node Reconnect (Reroute)
- 새 SessionId 생성
- 새 GameServer에서 세션 생성
- 기존 상태 손실 (제약사항)
- TODO: 기존 GameServer 세션 정리

#### 5. 응답 프로토콜
- CreateReconnectSuccessResponse: SessionId + Snapshot JSON

### 아키텍처 특징
- Same-Node: SessionId 재사용, 상태 유지
- Cross-Node: 새 SessionId, 상태 손실
- Snapshot: 현재 상태만 (Event Sourcing 아님)

### 빌드 검증
```bash
✅ GameServer.csproj - Build SUCCESS (0 warnings, 0 errors)
```

---

## Priority 5: Protocol Extensions ✅ COMPLETED

### 목표
- Sequence ID 추가 (Idempotency)
- 중복 패킷 감지 및 무시
- 프로토콜 버전 관리

### 구현 내용

#### 1. P2PProtocol에 SequenceId 추가
- **HeaderSize 증가**: 41 bytes → 49 bytes
- **P2PHeader.SequenceId**: 8 bytes (Idempotency용)
- **ProtocolVersion**: 상수 정의 (현재 버전 1)
- **Serialize/Deserialize 업데이트**: SequenceId 직렬화 추가

#### 2. PacketBuilder 업데이트
- CreateClientPacket: sequenceId 파라미터 추가 (기본값 0)
- CreateServerPacket: sequenceId 파라미터 추가 (기본값 0)
- 하위 호환성: sequenceId는 선택적 파라미터

#### 3. ISequenceIdStore 인터페이스 (네트워크 엔진 제공 레벨)
- **GetLastSequenceIdAsync**: 마지막 처리한 Sequence ID 조회
- **SetLastSequenceIdAsync**: Sequence ID 업데이트
- **IsProcessedAsync**: 중복 패킷 여부 확인

- **InMemorySequenceIdStore**: 인메모리 구현 (Dictionary)
- **설계 변경 사항**: Stateful 게임 서버 아키텍처 원칙에 따라, Redis 연동이나 프레임워크 단의 자동 Idempotency 처리는 제거됨. 재접속 및 상태 복구는 네트워크 라이브러리가 강제하지 않고 오직 게임 애플리케이션(유저) લે벨에서 필요에 따라 직접 구현하도록 인터페이스 구조만 제공.

#### 4. (Removed) IdempotencyMiddleware
- **상태 변경**: 프레임워크에서 제거됨
- **사유**: 패킷 유실 시 전체 상태를 응답하는 Stateful 구조에서는 프레임워크가 강제하는 Idempotency Pipeline이 오히려 방해가 됨. 게임 레이어에서 선택적으로 구현하도록 위임.

#### 5. ClientPacketContext 업데이트
- **SequenceId 필드 추가**: P2P 헤더에서 추출
- GatewayPacketListener에서 SequenceId 설정

#### 6. GameServerHub 통합
- PacketContext.Items에 SequenceId 추가
- IdempotencyMiddleware가 Items에서 SequenceId 추출

#### 7. Middleware Pipeline 순서
```
ExceptionHandling
    ↓
Logging
    ↓
Performance
    ↓
PacketHandler
```

#### 8. DI 구성
- `RedisSequenceIdStore` 및 `IdempotencyMiddleware` 기본 주입 코드 완전 제거.

### 아키텍처 특징

#### Idempotency 보장
- **At-Least-Once Delivery**: 네트워크 재전송으로 인한 중복 패킷 방지
- **SequenceId 검증**: 각 세션별로 마지막 처리 ID 추적
- **자동 무시**: 중복 패킷은 로그 후 무시, 응답 없음

#### 프로토콜 버전 관리
- **ProtocolVersion 상수**: 향후 호환성 관리
- **HeaderSize 명시**: 버전별 헤더 크기 구분

#### 성능 최적화
- **Redis 기반 저장**: 분산 환경에서 일관성 보장
- **TTL 24시간**: 오래된 세션 자동 정리
- **조건부 체크**: SequenceId 0이면 건너뛰기

### 빌드 검증
```bash
✅ GameServer.csproj - Build SUCCESS (0 warnings, 0 errors)
```

---

## 🎉 모든 Priority 완료!

### 완성된 기능
1. ✅ Priority 1: Gateway 세션별 라우팅
2. ✅ Priority 2: GameServer Redis 세션 관리
3. ✅ Priority 3: Service Mesh Kick-out Protocol
4. ✅ Priority 4: Reconnect Handling
5. ✅ Priority 5: Protocol Extensions (Sequence ID)
6. ✅ AOP Middleware Pattern

### 다음 단계 제안
- 클라이언트 구현 (Unity/Unreal)
- 부하 테스트 및 성능 튜닝
- 모니터링 및 메트릭 수집 (Prometheus)
- 분산 추적 (OpenTelemetry)
- 프로토콜 버전 2 설계
