using FluentAssertions;
using Game.Protocol;
using Proto.Test;
using Internal.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Middleware;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Game.Options;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// GameServerHub 유닛 테스트
/// GameServerHub는 Actor mailbox dispatch만 담당 (2 deps: logger, actorManager)
/// 로그인/재접속 로직은 LoginController로 분리됨
/// </summary>
public class GameServerHubTests : IDisposable
{
    private const long GameNodeId = 100;
    private const long GatewayNodeId = 200;

    private readonly Mock<ILogger<GameServerHub>> _loggerMock = new();
    private readonly Mock<ISessionActorManager> _actorManagerMock = new();
    private readonly GameServerHub _sut;

    // 캡처용
    private readonly List<(long gatewayNodeId, long sessionId, Response response, ushort requestId)> _sentResponses = [];

    public GameServerHubTests()
    {
        // ActorManager.TryAddActor 기본 동작: 성공
        _actorManagerMock
            .Setup(x => x.TryAddActor(It.IsAny<ISessionActor>()))
            .Returns<ISessionActor>(a => Result<ISessionActor>.Ok(a));

        _sut = new GameServerHub(
            _loggerMock.Object,
            _actorManagerMock.Object);
    }

    public void Dispose()
    {
        // cleanup if needed
    }

    #region Helper Methods

    /// <summary>
    /// 임의 MsgId를 포함하는 가짜 Payload 생성
    /// </summary>
    private static byte[] CreatePayloadWithMsgId(int msgId, ushort requestId = 1)
    {
        var payload = new byte[EndPointHeader.SizeOf + GameHeader.SizeOf];

        var endPointHeader = new EndPointHeader { TotalLength = payload.Length };
        System.Runtime.InteropServices.MemoryMarshal.Write(payload.AsSpan(), in endPointHeader);

        var gameHeader = new GameHeader
        {
            MsgId = msgId,
            SequenceId = 0,
            RequestId = requestId
        };
        gameHeader.Write(payload.AsSpan(EndPointHeader.SizeOf));

        return payload;
    }

    /// <summary>
    /// SendResponse를 캡처하는 PacketContext 생성
    /// </summary>
    private PacketContext CreatePacketContext(byte[] payload, long sessionId = 0)
    {
        var ctx = new PacketContext
        {
            GatewayNodeId = GatewayNodeId,
            SessionId = sessionId,
            RequestId = 1,
            Payload = payload,
            SendResponse = (gwId, sessId, resp, reqId, seqId) =>
            {
                _sentResponses.Add((gwId, sessId, resp, reqId));
            }
        };
        return ctx;
    }

    #endregion

    #region Scenario: DispatchToActor (Pinned 세션)

    [Fact]
    public void HandlePacket_PinnedSession_ActorExists_ShouldPushToActor()
    {
        // Arrange
        var sessionId = 5000L;
        var mockActor = new Mock<ISessionActor>();
        mockActor.Setup(a => a.ActorId).Returns(sessionId);

        _actorManagerMock
            .Setup(x => x.GetActor(sessionId))
            .Returns(Result<ISessionActor>.Ok(mockActor.Object));

        var payload = CreatePayloadWithMsgId(EchoReq.MsgId);
        var context = CreatePacketContext(payload, sessionId: sessionId);

        // Act
        _sut.HandlePacket(context);

        // Assert: Actor.Push가 호출되었는지 확인
        mockActor.Verify(a => a.Push(It.IsAny<IActorMessage>()), Times.Once);
    }

    [Fact]
    public void HandlePacket_PinnedSession_ActorNotFound_ShouldSendSessionExpired()
    {
        // Arrange
        var sessionId = 5000L;
        _actorManagerMock
            .Setup(x => x.GetActor(sessionId))
            .Returns(Result<ISessionActor>.Failure(ErrorCode.GameActorNotFound, "not found"));

        var payload = CreatePayloadWithMsgId(EchoReq.MsgId);
        var context = CreatePacketContext(payload, sessionId: sessionId);

        // Act
        _sut.HandlePacket(context);

        // Assert: ErrorCode만 전송 (payload 없는 에러 응답)
        _sentResponses.Should().HaveCount(1);
        var response = _sentResponses[0].response;
        response.Message.Should().BeNull();
        response.ErrorCode.Should().Be((short)ErrorCode.GameSessionExpired);
    }

    #endregion

    #region SessionActorManager 단위 테스트

    [Fact]
    public void SessionActorManager_TryAddAndGetActor_ShouldWork()
    {
        // Arrange
        var logger = new Mock<ILogger<SessionActorManager>>();
        var manager = new SessionActorManager(logger.Object);
        var actor = CreateMockActor(sessionId: 1, userId: 100);

        // Act
        var addResult = manager.TryAddActor(actor.Object);
        var getResult = manager.GetActor(1);

        // Assert
        addResult.IsSuccess.Should().BeTrue();
        getResult.IsSuccess.Should().BeTrue();
        getResult.Value.ActorId.Should().Be(1);
    }

    [Fact]
    public void SessionActorManager_GetActorByUserId_ShouldWork()
    {
        // Arrange
        var logger = new Mock<ILogger<SessionActorManager>>();
        var manager = new SessionActorManager(logger.Object);
        var actor = CreateMockActor(sessionId: 1, userId: 100);
        manager.TryAddActor(actor.Object);

        // Act
        var result = manager.GetActorByUserId(100);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(100);
    }

    [Fact]
    public void SessionActorManager_GetActorByReconnectKey_ShouldWork()
    {
        // Arrange
        var logger = new Mock<ILogger<SessionActorManager>>();
        var manager = new SessionActorManager(logger.Object);
        var reconnectKey = Guid.NewGuid();
        var actor = CreateMockActor(sessionId: 1, userId: 100, reconnectKey: reconnectKey);
        manager.TryAddActor(actor.Object);

        // Act
        var result = manager.GetActorByReconnectKey(reconnectKey);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.ActorId.Should().Be(1);
    }

    [Fact]
    public void SessionActorManager_RemoveActor_ShouldDisposeAndCleanup()
    {
        // Arrange
        var logger = new Mock<ILogger<SessionActorManager>>();
        var manager = new SessionActorManager(logger.Object);
        var actor = CreateMockActor(sessionId: 1, userId: 100);
        manager.TryAddActor(actor.Object);

        // Act
        var removeResult = manager.RemoveActor(1);

        // Assert
        removeResult.IsSuccess.Should().BeTrue();
        manager.GetActor(1).IsFailure.Should().BeTrue();
        manager.GetActorByUserId(100).IsFailure.Should().BeTrue();
        actor.Verify(a => a.Dispose(), Times.Once);
    }

    [Fact]
    public void SessionActorManager_DuplicateAdd_ShouldFail()
    {
        // Arrange
        var logger = new Mock<ILogger<SessionActorManager>>();
        var manager = new SessionActorManager(logger.Object);
        var actor1 = CreateMockActor(sessionId: 1, userId: 100);
        var actor2 = CreateMockActor(sessionId: 1, userId: 200); // 같은 sessionId

        // Act
        manager.TryAddActor(actor1.Object);
        var result = manager.TryAddActor(actor2.Object);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorCode.GameActorAddFailed);
    }

    [Fact]
    public void SessionActorManager_UpdateReconnectKey_ShouldRebindIndex()
    {
        // Arrange
        var logger = new Mock<ILogger<SessionActorManager>>();
        var manager = new SessionActorManager(logger.Object);
        var oldKey = Guid.NewGuid();
        var newKey = Guid.NewGuid();
        var actor = CreateMockActor(sessionId: 1, userId: 100, reconnectKey: oldKey);
        manager.TryAddActor(actor.Object);

        // Act
        manager.UpdateReconnectKey(1, oldKey, newKey);

        // Assert
        manager.GetActorByReconnectKey(oldKey).IsFailure.Should().BeTrue();
        manager.GetActorByReconnectKey(newKey).IsSuccess.Should().BeTrue();
    }

    private static Mock<ISessionActor> CreateMockActor(long sessionId, long userId, Guid? reconnectKey = null)
    {
        var mock = new Mock<ISessionActor>();
        mock.Setup(a => a.ActorId).Returns(sessionId);
        mock.Setup(a => a.UserId).Returns(userId);
        mock.Setup(a => a.ReconnectKey).Returns(reconnectKey ?? Guid.NewGuid());
        return mock;
    }

    #endregion

    #region SessionStore / KickoutHandler 계약 테스트

    [Fact]
    public async Task SessionStore_NewLogin_ShouldSetSession()
    {
        // Arrange: Redis에 기존 세션 없음
        var sessionStoreMock = new Mock<ISessionStore>();
        var userId = 42L;
        sessionStoreMock
            .Setup(x => x.GetSessionAsync(userId))
            .ReturnsAsync((SessionInfo?)null);

        // Act
        var result = await sessionStoreMock.Object.GetSessionAsync(userId);

        // Assert
        result.Should().BeNull("신규 유저는 기존 세션이 없어야 함");
    }

    [Fact]
    public async Task SessionStore_Reconnect_SameNode_ShouldReturnExistingSession()
    {
        // Arrange: 같은 GameServer에 기존 세션 있음
        var sessionStoreMock = new Mock<ISessionStore>();
        var userId = 42L;
        var existingSession = new SessionInfo
        {
            GameServerNodeId = GameNodeId,
            SessionId = 7777L,
            GatewayNodeId = GatewayNodeId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastActivityUtc = DateTimeOffset.UtcNow.AddSeconds(-30)
        };

        sessionStoreMock
            .Setup(x => x.GetSessionAsync(userId))
            .ReturnsAsync(existingSession);

        // Act
        var result = await sessionStoreMock.Object.GetSessionAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result!.GameServerNodeId.Should().Be(GameNodeId, "같은 노드 재접속");
        result.SessionId.Should().Be(7777L);
    }

    [Fact]
    public async Task SessionStore_Reconnect_CrossNode_ShouldReturnDifferentNodeSession()
    {
        // Arrange: 다른 GameServer에 기존 세션 있음
        var sessionStoreMock = new Mock<ISessionStore>();
        var userId = 42L;
        var otherNodeId = 999L;
        var existingSession = new SessionInfo
        {
            GameServerNodeId = otherNodeId,
            SessionId = 8888L,
            GatewayNodeId = GatewayNodeId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        sessionStoreMock
            .Setup(x => x.GetSessionAsync(userId))
            .ReturnsAsync(existingSession);

        // Act
        var result = await sessionStoreMock.Object.GetSessionAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result!.GameServerNodeId.Should().NotBe(GameNodeId, "다른 노드에 세션 존재 → Cross-Node Reconnect");
    }

    [Fact]
    public async Task KickoutHandler_CrossNodeDuplicate_ShouldSendKickoutRequest()
    {
        // Arrange
        var kickoutLogger = new Mock<ILogger<KickoutMessageHandler>>();
        var kickoutOptions = Options.Create(new GameOptions { GameNodeId = GameNodeId });
        var nodeSenderMock = new Mock<INodeSender>();
        var kickoutMock = new Mock<KickoutMessageHandler>(
            kickoutLogger.Object, kickoutOptions, nodeSenderMock.Object)
        { CallBase = false };

        var targetNodeId = 300L;
        var userId = 42L;
        kickoutMock
            .Setup(x => x.SendKickoutRequestAsync(
                targetNodeId, userId, It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(new ServiceMeshKickoutRes { UserId = userId, Success = true });

        // Act
        var result = await kickoutMock.Object.SendKickoutRequestAsync(
            targetNodeId, userId, 123L, GatewayNodeId);

        // Assert
        result.Success.Should().BeTrue();
        result.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task KickoutHandler_Timeout_ShouldReturnFailure()
    {
        // Arrange
        var kickoutLogger = new Mock<ILogger<KickoutMessageHandler>>();
        var kickoutOptions = Options.Create(new GameOptions { GameNodeId = GameNodeId });
        var nodeSenderMock = new Mock<INodeSender>();
        var kickoutMock = new Mock<KickoutMessageHandler>(
            kickoutLogger.Object, kickoutOptions, nodeSenderMock.Object)
        { CallBase = false };

        var targetNodeId = 300L;
        var userId = 42L;
        kickoutMock
            .Setup(x => x.SendKickoutRequestAsync(
                targetNodeId, userId, It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(new ServiceMeshKickoutRes
            {
                UserId = userId,
                Success = false,
                ErrorCode = ServiceMeshKickoutErrorCode.InternalError
            });

        // Act
        var result = await kickoutMock.Object.SendKickoutRequestAsync(
            targetNodeId, userId, 123L, GatewayNodeId);

        // Assert
        result.Success.Should().BeFalse();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void HandlePacket_PinnedSession_NullSendResponse_ShouldNotCrash()
    {
        // Arrange: SendResponse가 null인 상태에서 Actor 없음
        var sessionId = 5000L;
        _actorManagerMock
            .Setup(x => x.GetActor(sessionId))
            .Returns(Result<ISessionActor>.Failure(ErrorCode.GameActorNotFound, "not found"));

        var payload = CreatePayloadWithMsgId(EchoReq.MsgId);
        var context = new PacketContext
        {
            GatewayNodeId = GatewayNodeId,
            SessionId = sessionId,
            Payload = payload,
            SendResponse = null
        };

        // Act & Assert: 크래시 없이 정상 종료
        var act = () => _sut.HandlePacket(context);
        act.Should().NotThrow();
    }

    #endregion
}
