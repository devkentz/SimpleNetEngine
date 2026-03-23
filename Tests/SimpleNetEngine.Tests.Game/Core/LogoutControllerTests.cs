using FluentAssertions;
using Game.Protocol;
using Internal.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Controllers;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Game.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Node.Network;
using StackExchange.Redis;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// LoginController.HandleLogout 유닛 테스트
/// </summary>
public class LogoutControllerTests
{
    private const long GameNodeId = 100;
    private const long GatewayNodeId = 200;
    private const long SessionId = 5000;
    private const long UserId = 42;

    private readonly Mock<ISessionStore> _sessionStoreMock = new();
    private readonly Mock<ISessionActorManager> _actorManagerMock = new();
    private readonly Mock<ILoginHandler> _loginHandlerMock = new();
    private readonly Mock<INodeSender> _nodeSenderMock = new();
    private readonly Mock<IDatabase> _redisMock = new();
    private readonly LoginController _sut;

    public LogoutControllerTests()
    {
        var options = Options.Create(new GameOptions { GameNodeId = GameNodeId });

        var kickoutLogger = new Mock<ILogger<KickoutMessageHandler>>();
        var kickoutOptions = Options.Create(new GameOptions { GameNodeId = GameNodeId });
        var kickoutMock = new Mock<KickoutMessageHandler>(
            kickoutLogger.Object, kickoutOptions, _nodeSenderMock.Object)
        { CallBase = false };

        _loginHandlerMock
            .Setup(x => x.OnLogoutAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);

        _redisMock
            .Setup(x => x.LockTakeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _redisMock
            .Setup(x => x.LockReleaseAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        _actorManagerMock
            .Setup(x => x.UnregisterActor(It.IsAny<long>()))
            .Returns((long sid) => Result<ISessionActor>.Ok(Mock.Of<ISessionActor>()));

        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshDisconnectClientReq, ServiceMeshDisconnectClientRes>(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<ServiceMeshDisconnectClientReq>()))
            .ReturnsAsync(new ServiceMeshDisconnectClientRes { Success = true });

        _sut = new LoginController(
            Mock.Of<ILogger<LoginController>>(),
            _sessionStoreMock.Object,
            _actorManagerMock.Object,
            new ActorDisposeQueue(NullLogger<ActorDisposeQueue>.Instance),
            new GatewayDisconnectQueue(NullLogger<GatewayDisconnectQueue>.Instance),
            kickoutMock.Object,
            _nodeSenderMock.Object,
            _loginHandlerMock.Object,
            _redisMock.Object,
            Mock.Of<IClientSender>(),
            options,
            TimeProvider.System);
    }

    private static Mock<ISessionActor> CreateActiveActor()
    {
        var mock = new Mock<ISessionActor>();
        mock.Setup(a => a.ActorId).Returns(SessionId);
        mock.Setup(a => a.UserId).Returns(UserId);
        mock.Setup(a => a.GatewayNodeId).Returns(GatewayNodeId);
        mock.SetupProperty(a => a.Status, ActorState.Active);
        return mock;
    }

    [Fact]
    public async Task HandleLogout_ShouldCallOnLogoutAsync()
    {
        // Arrange
        var actor = CreateActiveActor();

        // Act
        await _sut.HandleLogout(actor.Object, new LogoutReq());

        // Assert
        _loginHandlerMock.Verify(
            x => x.OnLogoutAsync(actor.Object), Times.Once);
    }

    [Fact]
    public async Task HandleLogout_ShouldDeleteRedisSession()
    {
        // Arrange
        var actor = CreateActiveActor();

        // Act
        await _sut.HandleLogout(actor.Object, new LogoutReq());

        // Assert
        _sessionStoreMock.Verify(
            x => x.DeleteSessionAsync(UserId), Times.Once);
    }

    [Fact]
    public async Task HandleLogout_ShouldUnregisterActor()
    {
        // Arrange
        var actor = CreateActiveActor();

        // Act
        await _sut.HandleLogout(actor.Object, new LogoutReq());

        // Assert
        _actorManagerMock.Verify(
            x => x.UnregisterActor(SessionId), Times.Once);
    }

    [Fact]
    public async Task HandleLogout_ShouldReturnSuccess()
    {
        // Arrange
        var actor = CreateActiveActor();

        // Act
        var response = await _sut.HandleLogout(actor.Object, new LogoutReq());

        // Assert
        response.Message.Should().BeOfType<LogoutRes>();
        ((LogoutRes)response.Message!).Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleLogout_ShouldClearDisconnected()
    {
        // Arrange
        var actor = CreateActiveActor();

        // Act
        await _sut.HandleLogout(actor.Object, new LogoutReq());

        // Assert
        actor.Verify(a => a.ClearDisconnected(), Times.Once);
    }
}
