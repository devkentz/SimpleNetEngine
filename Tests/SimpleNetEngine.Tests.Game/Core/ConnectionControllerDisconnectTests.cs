using FluentAssertions;
using Internal.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Middleware;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Game.Options;
using SimpleNetEngine.Game.Services;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// ConnectionController.HandleClientDisconnected 유닛 테스트
/// - Active Actor: Disconnected 상태 전이 + OnDisconnectedAsync Hook + MarkDisconnected
/// - Created/Authenticating Actor: 즉시 제거
/// </summary>
public class ConnectionControllerDisconnectTests
{
    private const long SessionId = 5000;
    private const long GatewayNodeId = 200;

    private readonly Mock<ISessionActorManager> _actorManagerMock = new();
    private readonly Mock<ILoginHandler> _loginHandlerMock = new();
    private readonly Mock<ISessionStore> _sessionStoreMock = new();
    private readonly ConnectionController _sut;

    public ConnectionControllerDisconnectTests()
    {
        _loginHandlerMock
            .Setup(x => x.OnDisconnectedAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);
        _loginHandlerMock
            .Setup(x => x.OnLogoutAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);

        var options = Options.Create(new GameOptions { ReconnectGracePeriod = TimeSpan.FromSeconds(30) });

        var disconnectHandler = new ActorDisconnectHandler(
            _actorManagerMock.Object,
            new ActorDisposeQueue(NullLogger<ActorDisposeQueue>.Instance),
            _sessionStoreMock.Object,
            NullLogger<ActorDisconnectHandler>.Instance);

        _sut = new ConnectionController(
            Mock.Of<ILogger<ConnectionController>>(),
            _actorManagerMock.Object,
            new GatewayDisconnectQueue(NullLogger<GatewayDisconnectQueue>.Instance),
            disconnectHandler,
            _loginHandlerMock.Object,
            new SessionActorFactory(),
            Mock.Of<IMessageDispatcher>(),
            Mock.Of<IServiceScopeFactory>(),
            new MiddlewarePipelineFactory(Mock.Of<ILogger<MiddlewarePipeline>>()),
            null!); // GameSessionChannelListener은 HandleClientDisconnected에서 미사용
    }

    [Fact]
    public async Task HandleClientDisconnected_ActiveActor_ShouldTransitionToDisconnected()
    {
        // Arrange
        var actorMock = new Mock<ISessionActor>();
        actorMock.SetupProperty(a => a.Status, ActorState.Active);
        actorMock.Setup(a => a.UserId).Returns(42L);

        // ExecuteAsync가 콜백을 실행하도록 설정 (actor mailbox 시뮬레이션)
        actorMock
            .Setup(a => a.ExecuteAsync(It.IsAny<Func<IServiceProvider, Task>>()))
            .Returns<Func<IServiceProvider, Task>>(callback => callback(null!));

        _actorManagerMock
            .Setup(x => x.GetActor(SessionId))
            .Returns(Result<ISessionActor>.Ok(actorMock.Object));

        var req = new ServiceMeshClientDisconnectedNtfReq
        {
            SessionId = SessionId,
            GatewayNodeId = GatewayNodeId
        };

        // Act
        var res = await _sut.HandleClientDisconnected(req);

        // Assert: Disconnected 상태로 전이
        actorMock.Object.Status.Should().Be(ActorState.Disconnected);
        res.Success.Should().BeTrue();

        // OnDisconnectedAsync Hook 호출됨
        _loginHandlerMock.Verify(
            x => x.OnDisconnectedAsync(actorMock.Object), Times.Once);

        // MarkDisconnected 호출됨 (타임스탬프 기록)
        actorMock.Verify(a => a.MarkDisconnected(), Times.Once);

        // Actor 제거되지 않음 (InactivityScanner가 Grace Period 만료 시 처리)
        _actorManagerMock.Verify(x => x.RemoveActor(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task HandleClientDisconnected_CreatedActor_ShouldRemoveImmediately()
    {
        // Arrange
        var actorMock = new Mock<ISessionActor>();
        actorMock.SetupProperty(a => a.Status, ActorState.Created);

        _actorManagerMock
            .Setup(x => x.GetActor(SessionId))
            .Returns(Result<ISessionActor>.Ok(actorMock.Object));
        _actorManagerMock
            .Setup(x => x.RemoveActor(SessionId))
            .Returns(Result.Ok());

        var req = new ServiceMeshClientDisconnectedNtfReq
        {
            SessionId = SessionId,
            GatewayNodeId = GatewayNodeId
        };

        // Act
        var res = await _sut.HandleClientDisconnected(req);

        // Assert: 즉시 제거
        _actorManagerMock.Verify(x => x.RemoveActor(SessionId), Times.Once);
        res.Success.Should().BeTrue();

        // OnDisconnectedAsync 미호출 (유저 데이터 없음)
        _loginHandlerMock.Verify(
            x => x.OnDisconnectedAsync(It.IsAny<ISessionActor>()), Times.Never);
    }

    [Fact]
    public async Task HandleClientDisconnected_AuthenticatingActor_ShouldRemoveImmediately()
    {
        // Arrange
        var actorMock = new Mock<ISessionActor>();
        actorMock.SetupProperty(a => a.Status, ActorState.Authenticating);

        _actorManagerMock
            .Setup(x => x.GetActor(SessionId))
            .Returns(Result<ISessionActor>.Ok(actorMock.Object));
        _actorManagerMock
            .Setup(x => x.RemoveActor(SessionId))
            .Returns(Result.Ok());

        var req = new ServiceMeshClientDisconnectedNtfReq
        {
            SessionId = SessionId,
            GatewayNodeId = GatewayNodeId
        };

        // Act
        var res = await _sut.HandleClientDisconnected(req);

        // Assert: 즉시 제거, Hook 미호출
        _actorManagerMock.Verify(x => x.RemoveActor(SessionId), Times.Once);
        _loginHandlerMock.Verify(
            x => x.OnDisconnectedAsync(It.IsAny<ISessionActor>()), Times.Never);
    }

    [Fact]
    public async Task HandleClientDisconnected_ActorNotFound_ShouldReturnSuccess()
    {
        // Arrange: Actor가 이미 Kickout 등으로 정리된 경우 — 정상적인 레이스 컨디션
        _actorManagerMock
            .Setup(x => x.GetActor(SessionId))
            .Returns(Result<ISessionActor>.Failure(ErrorCode.GameActorNotFound, "not found"));

        var req = new ServiceMeshClientDisconnectedNtfReq
        {
            SessionId = SessionId,
            GatewayNodeId = GatewayNodeId
        };

        // Act
        var res = await _sut.HandleClientDisconnected(req);

        // Assert: 이미 정리된 Actor에 대한 disconnect는 성공으로 처리
        res.Success.Should().BeTrue();
    }
}
