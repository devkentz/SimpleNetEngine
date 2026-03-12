using FluentAssertions;
using Internal.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Game.Options;
using SimpleNetEngine.Game.Services;
using SimpleNetEngine.Game.Session;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// InactivityScanner.DisconnectClientAsync 유닛 테스트
/// - OnInactivityTimeoutAsync Hook에 따른 DisconnectAction 분기
/// - AllowSessionResume: Disconnected 전이 + MarkDisconnected + Gateway 종료
/// - TerminateSession: 즉시 Actor 제거 + Gateway 종료
/// </summary>
public class InactivityScannerTests
{
    private const long SessionId = 5000;
    private const long UserId = 42;
    private const long GatewayNodeId = 200;

    private readonly Mock<ISessionActorManager> _actorManagerMock = new();
    private readonly Mock<ILoginHandler> _loginHandlerMock = new();
    private readonly Mock<ISessionStore> _sessionStoreMock = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock = new();
    private readonly Mock<ILogger<InactivityScanner>> _loggerMock = new();
    private readonly IOptions<GameOptions> _options = Options.Create(new GameOptions
    {
        InactivityTimeout = TimeSpan.FromSeconds(60),
        ReconnectGracePeriod = TimeSpan.FromSeconds(30)
    });

    public InactivityScannerTests()
    {
        _loginHandlerMock
            .Setup(x => x.OnInactivityTimeoutAsync(It.IsAny<ISessionActor>()))
            .ReturnsAsync(DisconnectAction.AllowSessionResume);
        _loginHandlerMock
            .Setup(x => x.OnDisconnectedAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);
        _loginHandlerMock
            .Setup(x => x.OnLogoutAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);

        // IServiceScopeFactory → IServiceScope → IServiceProvider → ILoginHandler
        var scopeMock = new Mock<IServiceScope>();
        var spMock = new Mock<IServiceProvider>();
        spMock.Setup(sp => sp.GetService(typeof(ILoginHandler))).Returns(_loginHandlerMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(spMock.Object);
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);
    }

    private Mock<ISessionActor> CreateActorMock()
    {
        var actorMock = new Mock<ISessionActor>();
        actorMock.Setup(a => a.ActorId).Returns(SessionId);
        actorMock.Setup(a => a.UserId).Returns(UserId);
        actorMock.Setup(a => a.GatewayNodeId).Returns(GatewayNodeId);
        actorMock.SetupProperty(a => a.Status, ActorState.Active);

        actorMock
            .Setup(a => a.ExecuteAsync(It.IsAny<Func<IServiceProvider, Task>>()))
            .Returns<Func<IServiceProvider, Task>>(callback => callback(null!));

        return actorMock;
    }

    /// <summary>
    /// InactivityScanner는 BackgroundService라 DisconnectClientAsync가 private.
    /// 리플렉션으로 호출하여 테스트한다.
    /// </summary>
    private async Task InvokeDisconnectClientAsync(ISessionActor actor)
    {
        var disconnectHandler = new ActorDisconnectHandler(
            _actorManagerMock.Object,
            new ActorDisposeQueue(NullLogger<ActorDisposeQueue>.Instance),
            _sessionStoreMock.Object,
            NullLogger<ActorDisconnectHandler>.Instance);

        var scanner = new InactivityScanner(
            _actorManagerMock.Object,
            disconnectHandler,
            new GatewayDisconnectQueue(NullLogger<GatewayDisconnectQueue>.Instance),
            _scopeFactoryMock.Object,
            _options,
            _loggerMock.Object);

        var method = typeof(InactivityScanner).GetMethod(
            "DisconnectClientAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.Should().NotBeNull("DisconnectClientAsync method must exist");

        var task = (Task)method!.Invoke(scanner, [actor])!;
        await task;
    }

    [Fact]
    public async Task DisconnectClientAsync_AllowSessionResume_ShouldTransitionToDisconnected()
    {
        // Arrange
        var actorMock = CreateActorMock();

        // Act
        await InvokeDisconnectClientAsync(actorMock.Object);

        // Assert: Disconnected 전이 + MarkDisconnected
        actorMock.Object.Status.Should().Be(ActorState.Disconnected);
        actorMock.Verify(a => a.MarkDisconnected(), Times.Once);

        // OnDisconnectedAsync Hook 호출됨
        _loginHandlerMock.Verify(
            x => x.OnDisconnectedAsync(actorMock.Object), Times.Once);

        // Actor 즉시 제거 안 됨
        _actorManagerMock.Verify(x => x.UnregisterActor(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task DisconnectClientAsync_TerminateSession_ShouldRemoveActorImmediately()
    {
        // Arrange
        var actorMock = CreateActorMock();
        _loginHandlerMock
            .Setup(x => x.OnInactivityTimeoutAsync(It.IsAny<ISessionActor>()))
            .ReturnsAsync(DisconnectAction.TerminateSession);
        _loginHandlerMock
            .Setup(x => x.OnLogoutAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);

        _actorManagerMock
            .Setup(x => x.UnregisterActor(SessionId))
            .Returns(Result<ISessionActor>.Ok(Mock.Of<ISessionActor>()));

        // Act
        await InvokeDisconnectClientAsync(actorMock.Object);

        // Assert: 즉시 제거, MarkDisconnected 미호출
        _actorManagerMock.Verify(x => x.UnregisterActor(SessionId), Times.Once);
        actorMock.Verify(a => a.MarkDisconnected(), Times.Never);
    }

    [Fact]
    public async Task DisconnectClientAsync_TerminateSession_ShouldCallOnLogoutAsync()
    {
        // Arrange
        var actorMock = CreateActorMock();
        _loginHandlerMock
            .Setup(x => x.OnInactivityTimeoutAsync(It.IsAny<ISessionActor>()))
            .ReturnsAsync(DisconnectAction.TerminateSession);
        _loginHandlerMock
            .Setup(x => x.OnLogoutAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);

        _actorManagerMock
            .Setup(x => x.UnregisterActor(SessionId))
            .Returns(Result<ISessionActor>.Ok(Mock.Of<ISessionActor>()));

        // Act
        await InvokeDisconnectClientAsync(actorMock.Object);

        // Assert: OnLogoutAsync 호출됨
        _loginHandlerMock.Verify(x => x.OnLogoutAsync(actorMock.Object), Times.Once);
    }

    [Fact]
    public async Task DisconnectClientAsync_ShouldScheduleGatewayDisconnect()
    {
        // Arrange
        var actorMock = CreateActorMock();
        var disconnectQueue = new GatewayDisconnectQueue(NullLogger<GatewayDisconnectQueue>.Instance);

        var disconnectHandler = new ActorDisconnectHandler(
            _actorManagerMock.Object,
            new ActorDisposeQueue(NullLogger<ActorDisposeQueue>.Instance),
            _sessionStoreMock.Object,
            NullLogger<ActorDisconnectHandler>.Instance);

        var scanner = new InactivityScanner(
            _actorManagerMock.Object,
            disconnectHandler,
            disconnectQueue,
            _scopeFactoryMock.Object,
            _options,
            _loggerMock.Object);

        var method = typeof(InactivityScanner).GetMethod(
            "DisconnectClientAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var task = (Task)method!.Invoke(scanner, [actorMock.Object])!;
        await task;

        // Assert: GatewayDisconnectQueue에 예약됨
        var items = disconnectQueue.DrainAll();
        items.Should().ContainSingle(x => x.SessionId == SessionId && x.GatewayNodeId == GatewayNodeId);
    }

    [Fact]
    public async Task DisconnectClientAsync_ShouldCallOnInactivityTimeoutAsyncViaMailbox()
    {
        // Arrange
        var actorMock = CreateActorMock();

        // Act
        await InvokeDisconnectClientAsync(actorMock.Object);

        // Assert: actor.ExecuteAsync를 통해 호출됨
        actorMock.Verify(
            a => a.ExecuteAsync(It.IsAny<Func<IServiceProvider, Task>>()),
            Times.Once);

        _loginHandlerMock.Verify(
            x => x.OnInactivityTimeoutAsync(actorMock.Object), Times.Once);
    }
}
