using FluentAssertions;
using Internal.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Controllers;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Game.Options;
using SimpleNetEngine.Game.Services;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// KickoutController 유닛 테스트
/// - OnKickoutAsync Hook 호출 확인
/// - DisconnectAction 분기 (TerminateSession / AllowSessionResume)
/// - TerminateSession에서 조건부 삭제 (DeleteSessionIfMatchAsync) 확인
/// - Gateway disconnect는 GatewayDisconnectQueue로 예약
/// </summary>
public class KickoutControllerTests
{
    private const long SessionId = 5000;
    private const long UserId = 42;
    private const long GatewayNodeId = 200;

    private readonly Mock<ILogger<KickoutController>> _loggerMock = new();
    private readonly Mock<ISessionActorManager> _actorManagerMock = new();
    private readonly Mock<ILoginHandler> _loginHandlerMock = new();
    private readonly Mock<ISessionStore> _sessionStoreMock = new();
    private readonly GatewayDisconnectQueue _disconnectQueue =
        new(NullLogger<GatewayDisconnectQueue>.Instance);
    private readonly IOptions<GameOptions> _options = Options.Create(
        new GameOptions { ReconnectGracePeriod = TimeSpan.FromSeconds(30) });

    private KickoutController CreateSut()
    {
        var disconnectHandler = new ActorDisconnectHandler(
            _actorManagerMock.Object,
            new ActorDisposeQueue(NullLogger<ActorDisposeQueue>.Instance),
            _sessionStoreMock.Object,
            NullLogger<ActorDisconnectHandler>.Instance);

        return new KickoutController(
            _loggerMock.Object,
            _actorManagerMock.Object,
            disconnectHandler,
            _disconnectQueue,
            _loginHandlerMock.Object);
    }

    public KickoutControllerTests()
    {
        _loginHandlerMock
            .Setup(x => x.OnKickoutAsync(It.IsAny<ISessionActor>(), It.IsAny<KickoutReason>()))
            .ReturnsAsync(DisconnectAction.TerminateSession);
    }

    private static ServiceMeshKickoutReq CreateReq() => new()
    {
        UserId = UserId,
        SessionId = SessionId,
        GatewayNodeId = GatewayNodeId
    };

    private Mock<ISessionActor> CreateActorMock(ActorState status = ActorState.Active)
    {
        var actorMock = new Mock<ISessionActor>();
        actorMock.Setup(a => a.ActorId).Returns(SessionId);
        actorMock.Setup(a => a.UserId).Returns(UserId);
        actorMock.Setup(a => a.GatewayNodeId).Returns(GatewayNodeId);
        actorMock.SetupProperty(a => a.Status, status);

        actorMock
            .Setup(a => a.ExecuteAsync(It.IsAny<Func<IServiceProvider, Task>>()))
            .Returns<Func<IServiceProvider, Task>>(callback => callback(null!));

        _actorManagerMock
            .Setup(x => x.GetActor(SessionId))
            .Returns(Result<ISessionActor>.Ok(actorMock.Object));
        _actorManagerMock
            .Setup(x => x.UnregisterActor(SessionId))
            .Returns(Result<ISessionActor>.Ok(actorMock.Object));

        return actorMock;
    }

    [Fact]
    public async Task HandleKickout_TerminateSession_ShouldUnregisterActor()
    {
        // Arrange
        CreateActorMock();
        _loginHandlerMock
            .Setup(x => x.OnKickoutAsync(It.IsAny<ISessionActor>(), It.IsAny<KickoutReason>()))
            .ReturnsAsync(DisconnectAction.TerminateSession);

        // Act
        var res = await CreateSut().HandleKickout(CreateReq());

        // Assert: Actor UnregisterActor + DisposeQueue 위임
        _actorManagerMock.Verify(x => x.UnregisterActor(SessionId), Times.Once);
        res.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleKickout_TerminateSession_ShouldUseConditionalDelete()
    {
        // Arrange: cross-node kickout에서 조건부 삭제 사용
        CreateActorMock();
        _loginHandlerMock
            .Setup(x => x.OnLogoutAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);

        // Act
        await CreateSut().HandleKickout(CreateReq());

        // Assert: 무조건 DeleteSessionAsync가 아닌 조건부 DeleteSessionIfMatchAsync 호출
        _sessionStoreMock.Verify(x => x.DeleteSessionAsync(It.IsAny<long>()), Times.Never);
        _sessionStoreMock.Verify(x => x.DeleteSessionIfMatchAsync(UserId, SessionId), Times.Once);
        // ReconnectKey는 유니크 Guid이므로 항상 삭제
        _sessionStoreMock.Verify(x => x.DeleteReconnectKeyAsync(It.IsAny<Guid>()), Times.Once);
    }

    [Fact]
    public async Task HandleKickout_AllowSessionResume_ShouldTransitionToDisconnectedWithMarkDisconnected()
    {
        // Arrange
        var actorMock = CreateActorMock();
        _loginHandlerMock
            .Setup(x => x.OnKickoutAsync(It.IsAny<ISessionActor>(), It.IsAny<KickoutReason>()))
            .ReturnsAsync(DisconnectAction.AllowSessionResume);

        // Act
        var res = await CreateSut().HandleKickout(CreateReq());

        // Assert: Disconnected 전이 + MarkDisconnected
        actorMock.Object.Status.Should().Be(ActorState.Disconnected);
        actorMock.Verify(a => a.MarkDisconnected(), Times.Once);
        res.Success.Should().BeTrue();

        // Actor 즉시 제거 안 됨
        _actorManagerMock.Verify(x => x.UnregisterActor(SessionId), Times.Never);
    }

    [Fact]
    public async Task HandleKickout_AllowSessionResume_ShouldCallOnDisconnectedAsync()
    {
        // Arrange
        var actorMock = CreateActorMock();
        _loginHandlerMock
            .Setup(x => x.OnKickoutAsync(It.IsAny<ISessionActor>(), It.IsAny<KickoutReason>()))
            .ReturnsAsync(DisconnectAction.AllowSessionResume);
        _loginHandlerMock
            .Setup(x => x.OnDisconnectedAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);

        // Act
        await CreateSut().HandleKickout(CreateReq());

        // Assert: OnDisconnectedAsync Hook 호출됨
        _loginHandlerMock.Verify(
            x => x.OnDisconnectedAsync(actorMock.Object), Times.Once);
    }

    [Fact]
    public async Task HandleKickout_ShouldCallOnKickoutAsyncViaExecuteAsync()
    {
        // Arrange
        var actorMock = CreateActorMock();

        // Act
        var res = await CreateSut().HandleKickout(CreateReq());

        // Assert: OnKickoutAsync가 actor.ExecuteAsync를 통해 호출됨
        _loginHandlerMock.Verify(
            x => x.OnKickoutAsync(actorMock.Object, KickoutReason.DuplicateLogin),
            Times.Once);
        res.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleKickout_ActorNotFound_ShouldReturnFailure()
    {
        // Arrange
        _actorManagerMock
            .Setup(x => x.GetActor(SessionId))
            .Returns(Result<ISessionActor>.Failure(ErrorCode.GameActorNotFound, "not found"));

        // Act
        var res = await CreateSut().HandleKickout(CreateReq());

        // Assert
        res.Success.Should().BeFalse();
        res.ErrorCode.Should().Be(ServiceMeshKickoutErrorCode.UserNotFound);

        _loginHandlerMock.Verify(
            x => x.OnKickoutAsync(It.IsAny<ISessionActor>(), It.IsAny<KickoutReason>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleKickout_ShouldScheduleGatewayDisconnect()
    {
        // Arrange
        CreateActorMock();

        // Act
        await CreateSut().HandleKickout(CreateReq());

        // Assert: GatewayDisconnectQueue에 예약됨
        var items = _disconnectQueue.DrainAll();
        items.Should().ContainSingle(x => x.SessionId == SessionId && x.GatewayNodeId == GatewayNodeId);
    }
}
