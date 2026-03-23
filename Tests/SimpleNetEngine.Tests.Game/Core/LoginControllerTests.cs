using FluentAssertions;
using Game.Protocol;
using Internal.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Controllers;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Middleware;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Game.Options;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Protocol.Packets;
using StackExchange.Redis;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// LoginController 유닛 테스트
/// ILoginHandler Hook 통합 + 3가지 시나리오
/// </summary>
public class LoginControllerTests
{
    private const long GameNodeId = 100;
    private const long GatewayNodeId = 200;
    private const long SessionId = 5000;
    private const long UserId = 42;

    private readonly Mock<ILogger<LoginController>> _loggerMock = new();
    private readonly Mock<ISessionStore> _sessionStoreMock = new();
    private readonly Mock<ISessionActorManager> _actorManagerMock = new();
    private readonly Mock<KickoutMessageHandler> _kickoutMock;
    private readonly Mock<INodeSender> _nodeSenderMock = new();
    private readonly Mock<ILoginHandler> _loginHandlerMock = new();
    private readonly Mock<IDatabase> _redisMock = new();
    private readonly Mock<IClientSender> _clientSenderMock = new();
    private readonly LoginController _sut;

    public LoginControllerTests()
    {
        var options = Options.Create(new GameOptions { GameNodeId = GameNodeId });

        var kickoutLogger = new Mock<ILogger<KickoutMessageHandler>>();
        var kickoutOptions = Options.Create(new GameOptions { GameNodeId = GameNodeId });
        _kickoutMock = new Mock<KickoutMessageHandler>(
            kickoutLogger.Object, kickoutOptions, _nodeSenderMock.Object)
        { CallBase = false };

        // ILoginHandler 기본 설정: AuthenticateAsync 성공
        _loginHandlerMock
            .Setup(x => x.AuthenticateAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<ISessionActor>()))
            .ReturnsAsync(LoginAuthResult.Success(UserId));

        _loginHandlerMock
            .Setup(x => x.OnLoginSuccessAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);

        _loginHandlerMock
            .Setup(x => x.OnKickoutAsync(It.IsAny<ISessionActor>(), It.IsAny<EKickoutReason>()))
            .ReturnsAsync(DisconnectAction.TerminateSession);

        // Redis Lock 기본 설정: 항상 성공
        _redisMock
            .Setup(x => x.LockTakeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _redisMock
            .Setup(x => x.LockReleaseAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        _sut = new LoginController(
            _loggerMock.Object,
            _sessionStoreMock.Object,
            _actorManagerMock.Object,
            new ActorDisposeQueue(NullLogger<ActorDisposeQueue>.Instance),
            new GatewayDisconnectQueue(NullLogger<GatewayDisconnectQueue>.Instance),
            _kickoutMock.Object,
            _nodeSenderMock.Object,
            _loginHandlerMock.Object,
            _redisMock.Object,
            _clientSenderMock.Object,
            options,
            TimeProvider.System);
    }

    private static Mock<ISessionActor> CreateMockActor(
        long actorId = SessionId,
        long gatewayNodeId = GatewayNodeId)
    {
        var mock = new Mock<ISessionActor>();
        mock.Setup(a => a.ActorId).Returns(actorId);
        mock.Setup(a => a.GatewayNodeId).Returns(gatewayNodeId);
        mock.SetupProperty(a => a.UserId, 0L);
        mock.SetupProperty(a => a.Status, ActorState.Authenticating);
        mock.Setup(a => a.RegenerateReconnectKey()).Returns(Guid.NewGuid());
        return mock;
    }

    #region ILoginHandler Hook Tests

    [Fact]
    public async Task HandleLogin_ShouldCallAuthenticateAsync()
    {
        // Arrange
        var actor = CreateMockActor();
        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync((SessionInfo?)null);

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8("test-credential") };

        // Act
        await _sut.HandleLogin(actor.Object, req);

        // Assert: AuthenticateAsync가 credential과 actor로 호출됨
        _loginHandlerMock.Verify(
            x => x.AuthenticateAsync(It.IsAny<ReadOnlyMemory<byte>>(), actor.Object),
            Times.Once);
    }

    [Fact]
    public async Task HandleLogin_AuthFailed_ShouldReturnError()
    {
        // Arrange: 인증 실패
        var actor = CreateMockActor();
        _loginHandlerMock
            .Setup(x => x.AuthenticateAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<ISessionActor>()))
            .ReturnsAsync(LoginAuthResult.Failure(401, "Invalid token"));

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8("bad-token") };

        // Act
        var response = await _sut.HandleLogin(actor.Object, req);

        // Assert: 에러 반환, Actor 상태 변경 없음
        response.Message.Should().BeOfType<LoginGameRes>();
        var res = (LoginGameRes)response.Message!;
        res.Success.Should().BeFalse();

        actor.Object.Status.Should().Be(ActorState.Authenticating);
        _sessionStoreMock.Verify(
            x => x.SetSessionAsync(It.IsAny<long>(), It.IsAny<SessionInfo>(), It.IsAny<TimeSpan?>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleLogin_NewUser_ShouldCallOnLoginSuccessAsync()
    {
        // Arrange
        var actor = CreateMockActor();
        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync((SessionInfo?)null);

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        await _sut.HandleLogin(actor.Object, req);

        // Assert: OnLoginSuccessAsync 호출됨
        _loginHandlerMock.Verify(
            x => x.OnLoginSuccessAsync(actor.Object),
            Times.Once);
    }

    [Fact]
    public async Task HandleLogin_SameNodeDuplicate_ShouldCallOnKickoutAsyncViaExecuteAsync()
    {
        // Arrange
        var oldSessionId = 3000L;
        var actor = CreateMockActor();
        var oldActorMock = new Mock<ISessionActor>();

        // ExecuteAsync가 콜백을 실행하도록 설정 (actor mailbox 시뮬레이션)
        oldActorMock
            .Setup(a => a.ExecuteAsync(It.IsAny<Func<IServiceProvider, Task>>()))
            .Returns<Func<IServiceProvider, Task>>(callback => callback(null!));

        var existingSession = new SessionInfo
        {
            GameServerNodeId = GameNodeId,
            SessionId = oldSessionId,
            GatewayNodeId = GatewayNodeId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastActivityUtc = DateTimeOffset.UtcNow.AddSeconds(-30)
        };

        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(existingSession);

        _actorManagerMock
            .Setup(x => x.GetActor(oldSessionId))
            .Returns(Result<ISessionActor>.Ok(oldActorMock.Object));

        _actorManagerMock
            .Setup(x => x.RemoveActor(oldSessionId))
            .Returns(Result.Ok());

        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshDisconnectClientReq, ServiceMeshDisconnectClientRes>(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<ServiceMeshDisconnectClientReq>()))
            .ReturnsAsync(new ServiceMeshDisconnectClientRes { Success = true });

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        await _sut.HandleLogin(actor.Object, req);

        // Assert: OnKickoutAsync가 oldActor.ExecuteAsync를 통해 호출됨
        oldActorMock.Verify(
            a => a.ExecuteAsync(It.IsAny<Func<IServiceProvider, Task>>()),
            Times.Once,
            "OnKickoutAsync must be called through oldActor.ExecuteAsync for thread safety");

        _loginHandlerMock.Verify(
            x => x.OnKickoutAsync(oldActorMock.Object, EKickoutReason.DuplicateLogin),
            Times.Once);

        // OnLoginSuccessAsync도 새 Actor에 대해 호출됨
        _loginHandlerMock.Verify(
            x => x.OnLoginSuccessAsync(actor.Object),
            Times.Once);
    }

    [Fact]
    public async Task HandleLogin_LockFailed_ShouldReturnError()
    {
        // Arrange: 분산 락 획득 실패
        var actor = CreateMockActor();
        _redisMock
            .Setup(x => x.LockTakeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(false);

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        var response = await _sut.HandleLogin(actor.Object, req);

        // Assert: 에러 반환, Redis 세션 조회 없음
        response.Message.Should().BeOfType<LoginGameRes>();
        var res = (LoginGameRes)response.Message!;
        res.Success.Should().BeFalse();

        // 락 실패 시 세션 조회/저장 없어야 함
        _sessionStoreMock.Verify(
            x => x.GetSessionAsync(It.IsAny<long>()), Times.Never);
        _sessionStoreMock.Verify(
            x => x.SetSessionAsync(It.IsAny<long>(), It.IsAny<SessionInfo>(), It.IsAny<TimeSpan?>()),
            Times.Never);

        // Actor 상태 변경 없음
        actor.Object.Status.Should().Be(ActorState.Authenticating);
    }

    [Fact]
    public async Task HandleLogin_SameNodeDuplicate_ShouldSendKickoutNtf()
    {
        // Arrange
        var oldSessionId = 3000L;
        var actor = CreateMockActor();
        var oldActorMock = new Mock<ISessionActor>();
        oldActorMock.Setup(a => a.ActorId).Returns(oldSessionId);
        oldActorMock.Setup(a => a.GatewayNodeId).Returns(GatewayNodeId);
        oldActorMock.Setup(a => a.NextSequenceId()).Returns(1);
        oldActorMock
            .Setup(a => a.ExecuteAsync(It.IsAny<Func<IServiceProvider, Task>>()))
            .Returns<Func<IServiceProvider, Task>>(callback => callback(null!));

        var existingSession = new SessionInfo
        {
            GameServerNodeId = GameNodeId,
            SessionId = oldSessionId,
            GatewayNodeId = GatewayNodeId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastActivityUtc = DateTimeOffset.UtcNow.AddSeconds(-30)
        };

        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(existingSession);

        _actorManagerMock
            .Setup(x => x.GetActor(oldSessionId))
            .Returns(Result<ISessionActor>.Ok(oldActorMock.Object));

        _actorManagerMock
            .Setup(x => x.RemoveActor(oldSessionId))
            .Returns(Result.Ok());

        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshDisconnectClientReq, ServiceMeshDisconnectClientRes>(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<ServiceMeshDisconnectClientReq>()))
            .ReturnsAsync(new ServiceMeshDisconnectClientRes { Success = true });

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        await _sut.HandleLogin(actor.Object, req);

        // Assert: KickoutNtf가 기존 Actor에게 전송됨
        _clientSenderMock.Verify(
            x => x.SendNtf(
                It.Is<ISessionActor>(a => a.ActorId == oldSessionId),
                It.Is<Response>(r => r.Message is KickoutNtf)),
            Times.Once,
            "KickoutNtf must be sent to old client before actor removal");
    }

    #endregion

    #region Scenario 1: 신규 로그인

    [Fact]
    public async Task HandleLogin_NewUser_ShouldSetSessionAndActivate()
    {
        // Arrange: Redis에 기존 세션 없음
        var actor = CreateMockActor();
        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync((SessionInfo?)null);

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        var response = await _sut.HandleLogin(actor.Object, req);

        // Assert
        response.Message.Should().BeOfType<LoginGameRes>();
        var res = (LoginGameRes)response.Message!;
        res.Success.Should().BeTrue();

        // Actor 상태 전이 확인
        actor.Object.UserId.Should().Be(UserId);
        actor.Object.Status.Should().Be(ActorState.Active);
        actor.Verify(a => a.RegenerateReconnectKey(), Times.Once);

        // Redis SetSession 호출 확인
        _sessionStoreMock.Verify(x => x.SetSessionAsync(
            UserId,
            It.Is<SessionInfo>(s =>
                s.GameServerNodeId == GameNodeId &&
                s.SessionId == SessionId &&
                s.GatewayNodeId == GatewayNodeId),
            It.IsAny<TimeSpan?>()), Times.Once);
    }

    [Fact]
    public async Task HandleLogin_NewUser_ShouldNotCallKickout()
    {
        // Arrange
        var actor = CreateMockActor();
        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync((SessionInfo?)null);

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        await _sut.HandleLogin(actor.Object, req);

        // Assert: Kickout 미호출
        _kickoutMock.Verify(x => x.SendKickoutRequestAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>()),
            Times.Never);
    }

    #endregion

    #region Scenario 2: 동일 노드 중복 로그인

    [Fact]
    public async Task HandleLogin_SameNodeDuplicate_ShouldRemoveOldActorAndActivate()
    {
        // Arrange: 같은 노드에 기존 세션 존재
        var oldSessionId = 3000L;
        var actor = CreateMockActor();
        var existingSession = new SessionInfo
        {
            GameServerNodeId = GameNodeId,
            SessionId = oldSessionId,
            GatewayNodeId = GatewayNodeId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastActivityUtc = DateTimeOffset.UtcNow.AddSeconds(-30)
        };

        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(existingSession);

        _actorManagerMock
            .Setup(x => x.GetActor(oldSessionId))
            .Returns(Result<ISessionActor>.Ok(new Mock<ISessionActor>().Object));

        _actorManagerMock
            .Setup(x => x.RemoveActor(oldSessionId))
            .Returns(Result.Ok());

        // DisconnectClient RPC 성공
        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshDisconnectClientReq, ServiceMeshDisconnectClientRes>(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<ServiceMeshDisconnectClientReq>()))
            .ReturnsAsync(new ServiceMeshDisconnectClientRes { Success = true });

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        var response = await _sut.HandleLogin(actor.Object, req);

        // Assert
        response.Message.Should().BeOfType<LoginGameRes>();
        var res = (LoginGameRes)response.Message!;
        res.Success.Should().BeTrue();

        // 기존 Actor 제거 확인
        _actorManagerMock.Verify(x => x.RemoveActor(oldSessionId), Times.Once);

        // Actor 상태 전이
        actor.Object.Status.Should().Be(ActorState.Active);
        actor.Object.UserId.Should().Be(UserId);

        // Redis 갱신
        _sessionStoreMock.Verify(x => x.SetSessionAsync(
            UserId,
            It.Is<SessionInfo>(s => s.SessionId == SessionId),
            It.IsAny<TimeSpan?>()), Times.Once);
    }

    [Fact]
    public async Task HandleLogin_SameNodeDuplicate_SameSession_ShouldNotRemoveActor()
    {
        // Arrange: 같은 세션 ID (재접속 시나리오)
        var actor = CreateMockActor();
        var existingSession = new SessionInfo
        {
            GameServerNodeId = GameNodeId,
            SessionId = SessionId, // 동일 세션
            GatewayNodeId = GatewayNodeId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastActivityUtc = DateTimeOffset.UtcNow.AddSeconds(-30)
        };

        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(existingSession);

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        var response = await _sut.HandleLogin(actor.Object, req);

        // Assert: 같은 세션이므로 RemoveActor 미호출
        _actorManagerMock.Verify(x => x.RemoveActor(It.IsAny<long>()), Times.Never);
        response.Message.Should().BeOfType<LoginGameRes>();
    }

    [Fact]
    public async Task HandleLogin_SameNodeDuplicate_ShouldNotCallCrossNodeKickout()
    {
        // Arrange
        var actor = CreateMockActor();
        var existingSession = new SessionInfo
        {
            GameServerNodeId = GameNodeId,
            SessionId = 3000L,
            GatewayNodeId = GatewayNodeId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        };

        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(existingSession);

        _actorManagerMock
            .Setup(x => x.GetActor(3000L))
            .Returns(Result<ISessionActor>.Ok(new Mock<ISessionActor>().Object));

        _actorManagerMock
            .Setup(x => x.RemoveActor(3000L))
            .Returns(Result.Ok());

        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshDisconnectClientReq, ServiceMeshDisconnectClientRes>(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<ServiceMeshDisconnectClientReq>()))
            .ReturnsAsync(new ServiceMeshDisconnectClientRes { Success = true });

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        await _sut.HandleLogin(actor.Object, req);

        // Assert: 같은 노드이므로 Cross-Node Kickout 미호출
        _kickoutMock.Verify(x => x.SendKickoutRequestAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>()),
            Times.Never);
    }

    #endregion

    #region Scenario 3: 크로스 노드 중복 로그인

    [Fact]
    public async Task HandleLogin_CrossNodeDuplicate_ShouldKickoutAndActivate()
    {
        // Arrange: 다른 노드에 기존 세션 존재
        var otherNodeId = 999L;
        var actor = CreateMockActor();
        var existingSession = new SessionInfo
        {
            GameServerNodeId = otherNodeId,
            SessionId = 8888L,
            GatewayNodeId = GatewayNodeId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(existingSession);

        _kickoutMock
            .Setup(x => x.SendKickoutRequestAsync(otherNodeId, UserId, 8888L, GatewayNodeId))
            .ReturnsAsync(new ServiceMeshKickoutRes { UserId = UserId, Success = true });

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        var response = await _sut.HandleLogin(actor.Object, req);

        // Assert
        response.Message.Should().BeOfType<LoginGameRes>();
        var res = (LoginGameRes)response.Message!;
        res.Success.Should().BeTrue();

        // Kickout 호출 확인
        _kickoutMock.Verify(x => x.SendKickoutRequestAsync(
            otherNodeId, UserId, 8888L, GatewayNodeId), Times.Once);

        // Actor 상태 전이
        actor.Object.Status.Should().Be(ActorState.Active);
        actor.Object.UserId.Should().Be(UserId);

        // Redis 갱신 (새 노드 정보)
        _sessionStoreMock.Verify(x => x.SetSessionAsync(
            UserId,
            It.Is<SessionInfo>(s =>
                s.GameServerNodeId == GameNodeId &&
                s.SessionId == SessionId),
            It.IsAny<TimeSpan?>()), Times.Once);
    }

    [Fact]
    public async Task HandleLogin_CrossNodeDuplicate_KickoutFailed_ShouldStillLogin()
    {
        // Arrange: Kickout 실패해도 로그인은 진행
        var otherNodeId = 999L;
        var actor = CreateMockActor();
        var existingSession = new SessionInfo
        {
            GameServerNodeId = otherNodeId,
            SessionId = 8888L,
            GatewayNodeId = GatewayNodeId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        };

        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(existingSession);

        _kickoutMock
            .Setup(x => x.SendKickoutRequestAsync(otherNodeId, UserId, 8888L, GatewayNodeId))
            .ReturnsAsync(new ServiceMeshKickoutRes
            {
                UserId = UserId,
                Success = false,
                ErrorCode = ServiceMeshKickoutErrorCode.InternalError
            });

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        var response = await _sut.HandleLogin(actor.Object, req);

        // Assert: Kickout 실패해도 로그인 성공
        response.Message.Should().BeOfType<LoginGameRes>();
        ((LoginGameRes)response.Message!).Success.Should().BeTrue();
        actor.Object.Status.Should().Be(ActorState.Active);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task HandleLogin_SameNodeDuplicate_RemoveActorFailed_ShouldStillLogin()
    {
        // Arrange: RemoveActor 실패해도 로그인 진행
        var actor = CreateMockActor();
        var existingSession = new SessionInfo
        {
            GameServerNodeId = GameNodeId,
            SessionId = 3000L,
            GatewayNodeId = GatewayNodeId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        };

        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(existingSession);

        _actorManagerMock
            .Setup(x => x.GetActor(3000L))
            .Returns(Result<ISessionActor>.Failure(ErrorCode.GameActorNotFound, "not found"));

        _actorManagerMock
            .Setup(x => x.RemoveActor(3000L))
            .Returns(Result.Failure(ErrorCode.GameActorNotFound, "already removed"));

        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshDisconnectClientReq, ServiceMeshDisconnectClientRes>(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<ServiceMeshDisconnectClientReq>()))
            .ReturnsAsync(new ServiceMeshDisconnectClientRes { Success = true });

        var req = new LoginGameReq { Credential = Google.Protobuf.ByteString.CopyFromUtf8(UserId.ToString()) };

        // Act
        var response = await _sut.HandleLogin(actor.Object, req);

        // Assert: RemoveActor 실패해도 로그인 성공
        response.Message.Should().BeOfType<LoginGameRes>();
        ((LoginGameRes)response.Message!).Success.Should().BeTrue();
    }

    #endregion
}
