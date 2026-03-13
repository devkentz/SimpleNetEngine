using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Protocol.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SimpleNetEngine.Tests.Game.Actors;

public class ActorManagerTests
{
    private readonly SessionActorManager _actorManager;

    public ActorManagerTests()
    {
        _actorManager = new SessionActorManager(NullLogger<SessionActorManager>.Instance);
    }

    [Fact]
    public void GetActor_WhenNotExists_ReturnsFailure()
    {
        // Arrange
        var sessionId = 12345L;

        // Act
        var result = _actorManager.GetActor(sessionId);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.GameActorNotFound, result.Error);
        Assert.Contains("12345", result.ErrorMessage);
    }

    [Fact]
    public void TryAddActor_NewActor_ReturnsSuccess()
    {
        // Arrange
        var actor = new MockActor(actorId: 100, userId: 200);

        // Act
        var result = _actorManager.TryAddActor(actor);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(actor, result.Value);
        Assert.Equal(1, _actorManager.Count);
    }

    [Fact]
    public void TryAddActor_DuplicateActorId_ReturnsFailure()
    {
        // Arrange
        var actor1 = new MockActor(actorId: 100, userId: 200);
        var actor2 = new MockActor(actorId: 100, userId: 201); // Same ActorId, different UserId

        _actorManager.TryAddActor(actor1);

        // Act
        var result = _actorManager.TryAddActor(actor2);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.GameActorAddFailed, result.Error);
        Assert.Contains("100", result.ErrorMessage);
        Assert.Equal(1, _actorManager.Count); // Still only 1 actor
    }

    [Fact]
    public void GetActor_AfterAdd_ReturnsSuccess()
    {
        // Arrange
        var actor = new MockActor(actorId: 100, userId: 200);
        _actorManager.TryAddActor(actor);

        // Act
        var result = _actorManager.GetActor(100);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(actor, result.Value);
    }

    [Fact]
    public void GetActorByUserId_WhenNotExists_ReturnsFailure()
    {
        // Arrange
        var userId = 999L;

        // Act
        var result = _actorManager.GetActorByUserId(userId);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.GameActorNotFound, result.Error);
        Assert.Contains("999", result.ErrorMessage);
    }

    [Fact]
    public void GetActorByUserId_AfterAdd_ReturnsSuccess()
    {
        // Arrange
        var actor = new MockActor(actorId: 100, userId: 200);
        _actorManager.TryAddActor(actor);

        // Act
        var result = _actorManager.GetActorByUserId(200);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(actor, result.Value);
    }

    [Fact]
    public void GetActorByUserId_AfterUserIdRemap_ReturnsNewActor()
    {
        // Arrange
        var actor1 = new MockActor(actorId: 100, userId: 200);
        var actor2 = new MockActor(actorId: 101, userId: 200); // Same UserId, different ActorId

        _actorManager.TryAddActor(actor1);
        _actorManager.TryAddActor(actor2);

        // Act
        var result = _actorManager.GetActorByUserId(200);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(actor2, result.Value); // Should return the latest actor for this userId
    }

    [Fact]
    public void RemoveActor_WhenExists_ReturnsSuccess()
    {
        // Arrange
        var actor = new MockActor(actorId: 100, userId: 200);
        _actorManager.TryAddActor(actor);

        // Act
        var result = _actorManager.RemoveActor(100);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(0, _actorManager.Count);
        Assert.True(actor.IsDisposed);
    }

    [Fact]
    public void RemoveActor_WhenNotExists_ReturnsFailure()
    {
        // Arrange
        var sessionId = 999L;

        // Act
        var result = _actorManager.RemoveActor(sessionId);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.GameActorNotFound, result.Error);
        Assert.Contains("999", result.ErrorMessage);
    }

    [Fact]
    public void RemoveActor_CleansUpUserIdIndex()
    {
        // Arrange
        var actor = new MockActor(actorId: 100, userId: 200);
        _actorManager.TryAddActor(actor);

        // Act
        _actorManager.RemoveActor(100);

        // Assert
        var getUserResult = _actorManager.GetActorByUserId(200);
        Assert.True(getUserResult.IsFailure);
        Assert.Equal(ErrorCode.GameActorNotFound, getUserResult.Error);
    }

    // ==========================================================
    // RekeyActor Tests
    // ==========================================================

    [Fact]
    public void RekeyActor_Success_ChangesSessionIdMapping()
    {
        // Arrange
        var actor = new MockActor(actorId: 100, userId: 200);
        _actorManager.TryAddActor(actor);

        // Act
        var result = _actorManager.RekeyActor(oldSessionId: 100, newSessionId: 999);

        // Assert
        Assert.True(result.IsSuccess);

        // Old key should not find the actor
        Assert.True(_actorManager.GetActor(100).IsFailure);

        // New key should find the actor
        var getResult = _actorManager.GetActor(999);
        Assert.True(getResult.IsSuccess);
        Assert.Equal(actor, getResult.Value);

        // Actor's ActorId should be updated
        Assert.Equal(999, actor.ActorId);

        // Count unchanged
        Assert.Equal(1, _actorManager.Count);
    }

    [Fact]
    public void RekeyActor_UpdatesUserIdIndex()
    {
        // Arrange
        var actor = new MockActor(actorId: 100, userId: 200);
        _actorManager.TryAddActor(actor);

        // Act
        _actorManager.RekeyActor(oldSessionId: 100, newSessionId: 999);

        // Assert: UserId index should point to new SessionId
        var result = _actorManager.GetActorByUserId(200);
        Assert.True(result.IsSuccess);
        Assert.Equal(999, result.Value.ActorId);
    }

    [Fact]
    public void RekeyActor_UpdatesReconnectKeyIndex()
    {
        // Arrange
        var actor = new MockActor(actorId: 100, userId: 200);
        _actorManager.TryAddActor(actor);
        var reconnectKey = actor.ReconnectKey;

        // Act
        _actorManager.RekeyActor(oldSessionId: 100, newSessionId: 999);

        // Assert: ReconnectKey index should point to new SessionId
        var result = _actorManager.GetActorByReconnectKey(reconnectKey);
        Assert.True(result.IsSuccess);
        Assert.Equal(999, result.Value.ActorId);
    }

    [Fact]
    public void RekeyActor_OldSessionIdNotFound_ReturnsFailure()
    {
        // Act
        var result = _actorManager.RekeyActor(oldSessionId: 999, newSessionId: 100);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.GameActorNotFound, result.Error);
    }

    [Fact]
    public void RekeyActor_NewSessionIdAlreadyExists_ReturnsFailure()
    {
        // Arrange
        var actor1 = new MockActor(actorId: 100, userId: 200);
        var actor2 = new MockActor(actorId: 999, userId: 201);
        _actorManager.TryAddActor(actor1);
        _actorManager.TryAddActor(actor2);

        // Act: try to rekey actor1 to actor2's SessionId
        var result = _actorManager.RekeyActor(oldSessionId: 100, newSessionId: 999);

        // Assert
        Assert.True(result.IsFailure);

        // Original mappings should be unchanged
        Assert.True(_actorManager.GetActor(100).IsSuccess);
        Assert.True(_actorManager.GetActor(999).IsSuccess);
        Assert.Equal(100, actor1.ActorId); // ActorId not changed
    }

    [Fact]
    public void RekeyActor_DoesNotDisposeActor()
    {
        // Arrange
        var actor = new MockActor(actorId: 100, userId: 200);
        _actorManager.TryAddActor(actor);

        // Act
        _actorManager.RekeyActor(oldSessionId: 100, newSessionId: 999);

        // Assert: Actor should NOT be disposed (unlike RemoveActor)
        Assert.False(actor.IsDisposed);
    }

    [Fact]
    public void Count_ReflectsActorAdditionsAndRemovals()
    {
        // Arrange
        var actor1 = new MockActor(actorId: 100, userId: 200);
        var actor2 = new MockActor(actorId: 101, userId: 201);

        // Act & Assert
        Assert.Equal(0, _actorManager.Count);

        _actorManager.TryAddActor(actor1);
        Assert.Equal(1, _actorManager.Count);

        _actorManager.TryAddActor(actor2);
        Assert.Equal(2, _actorManager.Count);

        _actorManager.RemoveActor(100);
        Assert.Equal(1, _actorManager.Count);

        _actorManager.RemoveActor(101);
        Assert.Equal(0, _actorManager.Count);
    }
}

/// <summary>
/// Mock implementation of IActor for testing
/// </summary>
internal class MockActor : ISessionActor
{
    public long ActorId { get; set; }
    public long UserId { get; set; }
    public long GatewayNodeId { get; private set; }
    public ActorState Status { get; set; }
    public Dictionary<string, object> State { get; } = [];
    private long _sequenceId;
    private Guid _reconnectKey;
    public long SequenceId => _sequenceId;
    public Guid ReconnectKey => _reconnectKey;

    public bool IsDisposed { get; private set; }

    public MockActor(long actorId, long userId, long gatewayNodeId = 1)
    {
        ActorId = actorId;
        UserId = userId;
        GatewayNodeId = gatewayNodeId;
        _sequenceId = 0;
        _reconnectKey = Guid.NewGuid();
    }

    public void Push(IActorMessage message)
    {
        // Mock implementation
    }

    public void UpdateRouting(long gatewayNodeId)
    {
        GatewayNodeId = gatewayNodeId;
    }

    public long NextSequenceId()
    {
        return Interlocked.Increment(ref _sequenceId);
    }

    public Guid RegenerateReconnectKey()
    {
        _reconnectKey = Guid.NewGuid();
        return _reconnectKey;
    }

    public long LastActivityTicks => Environment.TickCount64;

    public long DisconnectedTicks { get; private set; }

    public void TouchActivity() { }

    public void MarkDisconnected() { DisconnectedTicks = Environment.TickCount64; }

    public void ClearDisconnected() { DisconnectedTicks = 0; }

    private ushort _lastClientSequenceId;
    public ushort LastClientSequenceId => _lastClientSequenceId;

    public bool ValidateAndUpdateSequenceId(ushort clientSeqId)
    {
        if (clientSeqId <= _lastClientSequenceId && _lastClientSequenceId != 0)
            return false;
        _lastClientSequenceId = clientSeqId;
        return true;
    }

    public Task ExecuteAsync(Func<IServiceProvider, Task> action) => Task.CompletedTask;

    public virtual void Dispose()
    {
        IsDisposed = true;
    }
}
