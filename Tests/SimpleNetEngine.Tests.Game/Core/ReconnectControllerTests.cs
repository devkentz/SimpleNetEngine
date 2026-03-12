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
using SimpleNetEngine.Game.Options;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// ReconnectController 유닛 테스트
/// Same-Node / Cross-Node 재접속 시나리오
/// </summary>
public class ReconnectControllerTests
{
    private const long GameNodeId = 100;
    private const long GatewayNodeId = 200;
    private const long TempSessionId = 9000;
    private const long ExistingSessionId = 5000;
    private const long UserId = 42;

    private readonly Mock<ISessionStore> _sessionStoreMock = new();
    private readonly Mock<ISessionActorManager> _actorManagerMock = new();
    private readonly Mock<ILoginHandler> _loginHandlerMock = new();
    private readonly Mock<INodeSender> _nodeSenderMock = new();
    private readonly ReconnectController _sut;
    private readonly Guid _reconnectKey = Guid.NewGuid();

    public ReconnectControllerTests()
    {
        var options = Options.Create(new GameOptions { GameNodeId = GameNodeId });

        _loginHandlerMock
            .Setup(x => x.OnReconnectedAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);
        _loginHandlerMock
            .Setup(x => x.OnLoginSuccessAsync(It.IsAny<ISessionActor>()))
            .Returns(Task.CompletedTask);

        _sut = new ReconnectController(
            Mock.Of<ILogger<ReconnectController>>(),
            _sessionStoreMock.Object,
            _actorManagerMock.Object,
            new ActorDisposeQueue(NullLogger<ActorDisposeQueue>.Instance),
            _nodeSenderMock.Object,
            _loginHandlerMock.Object,
            options,
            TimeProvider.System);
    }

    private Mock<ISessionActor> CreateTempActor()
    {
        var mock = new Mock<ISessionActor>();
        mock.Setup(a => a.ActorId).Returns(TempSessionId);
        mock.Setup(a => a.GatewayNodeId).Returns(GatewayNodeId);
        mock.SetupProperty(a => a.Status, ActorState.Authenticating);
        mock.SetupProperty(a => a.UserId, 0L);
        mock.Setup(a => a.RegenerateReconnectKey()).Returns(Guid.NewGuid());
        return mock;
    }

    #region Invalid Key

    [Fact]
    public async Task HandleReconnect_InvalidKeyFormat_ShouldReturnError()
    {
        var actor = CreateTempActor();
        var req = new ReconnectReq { ReconnectKey = "not-a-guid" };

        var response = await _sut.HandleReconnect(actor.Object, req);

        var res = (ReconnectRes)response.Message!;
        res.Success.Should().BeFalse();
    }

    [Fact]
    public async Task HandleReconnect_KeyNotInRedis_ShouldReturnError()
    {
        var actor = CreateTempActor();
        _sessionStoreMock
            .Setup(x => x.GetUserIdByReconnectKeyAsync(It.IsAny<Guid>()))
            .ReturnsAsync((long?)null);

        var req = new ReconnectReq { ReconnectKey = _reconnectKey.ToString() };

        var response = await _sut.HandleReconnect(actor.Object, req);

        var res = (ReconnectRes)response.Message!;
        res.Success.Should().BeFalse();
    }

    [Fact]
    public async Task HandleReconnect_SessionNotInRedis_ShouldReturnError()
    {
        var actor = CreateTempActor();
        _sessionStoreMock
            .Setup(x => x.GetUserIdByReconnectKeyAsync(It.IsAny<Guid>()))
            .ReturnsAsync(UserId);
        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync((SessionInfo?)null);

        var req = new ReconnectReq { ReconnectKey = _reconnectKey.ToString() };

        var response = await _sut.HandleReconnect(actor.Object, req);

        var res = (ReconnectRes)response.Message!;
        res.Success.Should().BeFalse();
    }

    #endregion

    #region Same-Node Reconnect

    [Fact]
    public async Task HandleReconnect_SameNode_ShouldRestoreActor()
    {
        // Arrange
        var tempActor = CreateTempActor();
        var existingActorMock = new Mock<ISessionActor>();
        existingActorMock.SetupProperty(a => a.Status, ActorState.Disconnected);
        existingActorMock.Setup(a => a.ActorId).Returns(ExistingSessionId);
        existingActorMock.Setup(a => a.RegenerateReconnectKey()).Returns(Guid.NewGuid());

        _sessionStoreMock
            .Setup(x => x.GetUserIdByReconnectKeyAsync(It.IsAny<Guid>()))
            .ReturnsAsync(UserId);
        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(new SessionInfo
            {
                GameServerNodeId = GameNodeId, // Same node
                SessionId = ExistingSessionId,
                GatewayNodeId = 300, // Old gateway
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            });

        _actorManagerMock
            .Setup(x => x.GetActor(ExistingSessionId))
            .Returns(Result<ISessionActor>.Ok(existingActorMock.Object));
        _actorManagerMock
            .Setup(x => x.UnregisterActor(TempSessionId))
            .Returns(Result<ISessionActor>.Ok(Mock.Of<ISessionActor>()));

        var req = new ReconnectReq { ReconnectKey = _reconnectKey.ToString() };

        // Act
        var response = await _sut.HandleReconnect(tempActor.Object, req);

        // Assert
        var res = (ReconnectRes)response.Message!;
        res.Success.Should().BeTrue();
        res.NewReconnectKey.Should().NotBeNullOrEmpty();

        // Actor 복원됨
        existingActorMock.Object.Status.Should().Be(ActorState.Active);
        existingActorMock.Verify(a => a.CancelGracePeriod(), Times.Once);
        existingActorMock.Verify(a => a.UpdateRouting(GatewayNodeId), Times.Once);
        existingActorMock.Verify(a => a.RegenerateReconnectKey(), Times.Once);

        // OnReconnectedAsync 호출됨
        _loginHandlerMock.Verify(
            x => x.OnReconnectedAsync(existingActorMock.Object), Times.Once);

        // 임시 Actor 제거됨 (UnregisterActor + DisposeQueue)
        _actorManagerMock.Verify(x => x.UnregisterActor(TempSessionId), Times.Once);

        // Old ReconnectKey 삭제됨
        _sessionStoreMock.Verify(
            x => x.DeleteReconnectKeyAsync(It.IsAny<Guid>()), Times.Once);
    }

    [Fact]
    public async Task HandleReconnect_SameNode_ActorNotDisconnected_ShouldReturnError()
    {
        // Arrange: Actor가 Active 상태 (정상적이지 않음)
        var tempActor = CreateTempActor();
        var existingActorMock = new Mock<ISessionActor>();
        existingActorMock.SetupProperty(a => a.Status, ActorState.Active);

        _sessionStoreMock
            .Setup(x => x.GetUserIdByReconnectKeyAsync(It.IsAny<Guid>()))
            .ReturnsAsync(UserId);
        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(new SessionInfo
            {
                GameServerNodeId = GameNodeId,
                SessionId = ExistingSessionId,
                GatewayNodeId = GatewayNodeId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                LastActivityUtc = DateTimeOffset.UtcNow
            });

        _actorManagerMock
            .Setup(x => x.GetActor(ExistingSessionId))
            .Returns(Result<ISessionActor>.Ok(existingActorMock.Object));

        var req = new ReconnectReq { ReconnectKey = _reconnectKey.ToString() };

        // Act
        var response = await _sut.HandleReconnect(tempActor.Object, req);

        // Assert
        var res = (ReconnectRes)response.Message!;
        res.Success.Should().BeFalse();
    }

    #endregion

    #region Cross-Node Reconnect (Re-route)

    [Fact]
    public async Task HandleReconnect_CrossNode_ShouldPreCreateActorAndRerouteAndReturnRetry()
    {
        // Arrange
        var tempActor = CreateTempActor();
        var otherNodeId = 999L;

        _sessionStoreMock
            .Setup(x => x.GetUserIdByReconnectKeyAsync(It.IsAny<Guid>()))
            .ReturnsAsync(UserId);
        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(new SessionInfo
            {
                GameServerNodeId = otherNodeId,
                SessionId = ExistingSessionId,
                GatewayNodeId = 300,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            });

        // Step 1: NtfNewUser (is_reroute=true) to target node
        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshNewUserNtfReq, ServiceMeshNewUserNtfRes>(
                NodePacket.ServerActorId,
                otherNodeId,
                It.Is<ServiceMeshNewUserNtfReq>(r =>
                    r.SessionId == TempSessionId &&
                    r.GatewayNodeId == GatewayNodeId &&
                    r.IsReroute == true)))
            .ReturnsAsync(new ServiceMeshNewUserNtfRes { Success = true });

        // Step 2: Reroute to Gateway
        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshRerouteSocketReq, ServiceMeshRerouteSocketRes>(
                NodePacket.ServerActorId,
                GatewayNodeId,
                It.Is<ServiceMeshRerouteSocketReq>(r =>
                    r.SessionId == TempSessionId && r.TargetNodeId == otherNodeId)))
            .ReturnsAsync(new ServiceMeshRerouteSocketRes { Success = true });

        var req = new ReconnectReq { ReconnectKey = _reconnectKey.ToString() };

        // Act
        var response = await _sut.HandleReconnect(tempActor.Object, req);

        // Assert
        var res = (ReconnectRes)response.Message!;
        res.Success.Should().BeFalse();
        res.RequiresRetry.Should().BeTrue();
        res.ErrorMessage.Should().Be("SESSION_ON_OTHER_NODE");

        // NtfNewUser(is_reroute=true) 호출됨 (Reroute 전에)
        _nodeSenderMock.Verify(
            x => x.RequestAsync<ServiceMeshNewUserNtfReq, ServiceMeshNewUserNtfRes>(
                NodePacket.ServerActorId,
                otherNodeId,
                It.Is<ServiceMeshNewUserNtfReq>(r => r.IsReroute)),
            Times.Once);

        // Re-route RPC 호출됨
        _nodeSenderMock.Verify(
            x => x.RequestAsync<ServiceMeshRerouteSocketReq, ServiceMeshRerouteSocketRes>(
                NodePacket.ServerActorId,
                GatewayNodeId,
                It.IsAny<ServiceMeshRerouteSocketReq>()),
            Times.Once);

        // 임시 Actor 제거됨 (UnregisterActor + DisposeQueue)
        _actorManagerMock.Verify(x => x.UnregisterActor(TempSessionId), Times.Once);

        // ReconnectKey는 삭제되지 않음 (기존 노드에서 사용해야 함)
        _sessionStoreMock.Verify(
            x => x.DeleteReconnectKeyAsync(It.IsAny<Guid>()), Times.Never);

        // Kickout, OnLoginSuccess, OnReconnected는 호출되지 않음
        _loginHandlerMock.Verify(
            x => x.OnLoginSuccessAsync(It.IsAny<ISessionActor>()), Times.Never);
        _loginHandlerMock.Verify(
            x => x.OnReconnectedAsync(It.IsAny<ISessionActor>()), Times.Never);
    }

    [Fact]
    public async Task HandleReconnect_CrossNode_NtfNewUserFailed_ShouldReturnError()
    {
        // Arrange: NtfNewUser RPC가 실패하면 Reroute 없이 에러 반환
        var tempActor = CreateTempActor();
        var otherNodeId = 999L;

        _sessionStoreMock
            .Setup(x => x.GetUserIdByReconnectKeyAsync(It.IsAny<Guid>()))
            .ReturnsAsync(UserId);
        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(new SessionInfo
            {
                GameServerNodeId = otherNodeId,
                SessionId = ExistingSessionId,
                GatewayNodeId = 300,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            });

        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshNewUserNtfReq, ServiceMeshNewUserNtfRes>(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<ServiceMeshNewUserNtfReq>()))
            .ReturnsAsync(new ServiceMeshNewUserNtfRes { Success = false });

        var req = new ReconnectReq { ReconnectKey = _reconnectKey.ToString() };

        // Act
        var response = await _sut.HandleReconnect(tempActor.Object, req);

        // Assert
        var res = (ReconnectRes)response.Message!;
        res.Success.Should().BeFalse();
        res.RequiresRetry.Should().BeFalse();
        res.ErrorMessage.Should().Be("Cross-node actor creation failed");

        // Reroute는 호출되지 않아야 함
        _nodeSenderMock.Verify(
            x => x.RequestAsync<ServiceMeshRerouteSocketReq, ServiceMeshRerouteSocketRes>(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<ServiceMeshRerouteSocketReq>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleReconnect_CrossNode_RerouteFailed_ShouldReturnError()
    {
        // Arrange: NtfNewUser 성공 후 Reroute 실패
        var tempActor = CreateTempActor();
        var otherNodeId = 999L;

        _sessionStoreMock
            .Setup(x => x.GetUserIdByReconnectKeyAsync(It.IsAny<Guid>()))
            .ReturnsAsync(UserId);
        _sessionStoreMock
            .Setup(x => x.GetSessionAsync(UserId))
            .ReturnsAsync(new SessionInfo
            {
                GameServerNodeId = otherNodeId,
                SessionId = ExistingSessionId,
                GatewayNodeId = 300,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            });

        // NtfNewUser 성공
        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshNewUserNtfReq, ServiceMeshNewUserNtfRes>(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<ServiceMeshNewUserNtfReq>()))
            .ReturnsAsync(new ServiceMeshNewUserNtfRes { Success = true });

        // Reroute 실패
        _nodeSenderMock
            .Setup(x => x.RequestAsync<ServiceMeshRerouteSocketReq, ServiceMeshRerouteSocketRes>(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<ServiceMeshRerouteSocketReq>()))
            .ThrowsAsync(new TimeoutException("RPC timeout"));

        var req = new ReconnectReq { ReconnectKey = _reconnectKey.ToString() };

        // Act
        var response = await _sut.HandleReconnect(tempActor.Object, req);

        // Assert
        var res = (ReconnectRes)response.Message!;
        res.Success.Should().BeFalse();
        res.RequiresRetry.Should().BeFalse();
        res.ErrorMessage.Should().Be("Re-route failed");
    }

    #endregion
}
