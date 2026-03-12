using SimpleNetEngine.Game.Actor;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SimpleNetEngine.Tests.Game.Actors;

public class ActorDisposeQueueTests
{
    private readonly ActorDisposeQueue _queue;

    public ActorDisposeQueueTests()
    {
        _queue = new ActorDisposeQueue(NullLogger<ActorDisposeQueue>.Instance);
    }

    [Fact]
    public void Enqueue_AddsActorToQueue()
    {
        // Arrange
        var actor = new MockActor(actorId: 100, userId: 200);

        // Act
        _queue.Enqueue(actor);

        // Assert
        Assert.Equal(1, _queue.PendingCount);
    }

    [Fact]
    public void Enqueue_MultipleTimes_AccumulatesCount()
    {
        // Arrange
        var actor1 = new MockActor(actorId: 100, userId: 200);
        var actor2 = new MockActor(actorId: 101, userId: 201);

        // Act
        _queue.Enqueue(actor1);
        _queue.Enqueue(actor2);

        // Assert
        Assert.Equal(2, _queue.PendingCount);
    }

    [Fact]
    public void DrainAndDispose_DisposesAllEnqueuedActors()
    {
        // Arrange
        var actor1 = new MockActor(actorId: 100, userId: 200);
        var actor2 = new MockActor(actorId: 101, userId: 201);
        _queue.Enqueue(actor1);
        _queue.Enqueue(actor2);

        // Act
        var disposed = _queue.DrainAndDispose();

        // Assert
        Assert.Equal(2, disposed);
        Assert.True(actor1.IsDisposed);
        Assert.True(actor2.IsDisposed);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Fact]
    public void DrainAndDispose_WhenEmpty_ReturnsZero()
    {
        // Act
        var disposed = _queue.DrainAndDispose();

        // Assert
        Assert.Equal(0, disposed);
    }

    [Fact]
    public void DrainAndDispose_ContinuesOnDisposeException()
    {
        // Arrange: actor2 throws on Dispose, but actor1 and actor3 should still be disposed
        var actor1 = new MockActor(actorId: 100, userId: 200);
        var actor2 = new ThrowingDisposeActor(actorId: 101, userId: 201);
        var actor3 = new MockActor(actorId: 102, userId: 202);

        _queue.Enqueue(actor1);
        _queue.Enqueue(actor2);
        _queue.Enqueue(actor3);

        // Act
        var disposed = _queue.DrainAndDispose();

        // Assert: all 3 processed (even though actor2 threw)
        Assert.Equal(3, disposed);
        Assert.True(actor1.IsDisposed);
        Assert.True(actor3.IsDisposed);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Fact]
    public void Enqueue_AfterDrain_WorksCorrectly()
    {
        // Arrange
        var actor1 = new MockActor(actorId: 100, userId: 200);
        _queue.Enqueue(actor1);
        _queue.DrainAndDispose();

        // Act: enqueue new actor after drain
        var actor2 = new MockActor(actorId: 101, userId: 201);
        _queue.Enqueue(actor2);
        var disposed = _queue.DrainAndDispose();

        // Assert
        Assert.Equal(1, disposed);
        Assert.True(actor2.IsDisposed);
    }
}

/// <summary>
/// Actor that throws on Dispose (for testing error resilience)
/// </summary>
internal class ThrowingDisposeActor : MockActor
{
    public ThrowingDisposeActor(long actorId, long userId) : base(actorId, userId) { }

    public override void Dispose()
    {
        throw new InvalidOperationException("Simulated dispose failure");
    }
}
