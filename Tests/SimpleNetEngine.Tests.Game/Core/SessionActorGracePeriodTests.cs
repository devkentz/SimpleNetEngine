using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Middleware;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// SessionActor.StartGracePeriod / CancelGracePeriod 유닛 테스트
/// </summary>
public class SessionActorGracePeriodTests
{
    private static SessionActor CreateActor()
    {
        return new SessionActor(
            actorId: 1000,
            userId: 42,
            gatewayNodeId: 200,
            scopeFactory: Mock.Of<IServiceScopeFactory>(),
            dispatcher: Mock.Of<IMessageDispatcher>(),
            pipeline: new MiddlewarePipelineFactory(Mock.Of<ILogger<MiddlewarePipeline>>()).CreateDefaultPipeline(),
            logger: Mock.Of<ILogger>());
    }

    [Fact]
    public async Task StartGracePeriod_ShouldCallOnExpiredAfterDuration()
    {
        // Arrange
        using var actor = CreateActor();
        var expired = new TaskCompletionSource<bool>();

        // Act: 100ms grace period
        actor.StartGracePeriod(TimeSpan.FromMilliseconds(100), () =>
        {
            expired.SetResult(true);
            return Task.CompletedTask;
        });

        // Assert: 콜백이 호출됨
        var result = await Task.WhenAny(expired.Task, Task.Delay(5000));
        result.Should().Be(expired.Task, "Grace period callback should fire");
        (await expired.Task).Should().BeTrue();
    }

    [Fact]
    public async Task CancelGracePeriod_ShouldPreventCallback()
    {
        // Arrange
        using var actor = CreateActor();
        var expired = false;

        // Act: Grace period 시작 후 즉시 취소
        actor.StartGracePeriod(TimeSpan.FromMilliseconds(100), () =>
        {
            expired = true;
            return Task.CompletedTask;
        });
        actor.CancelGracePeriod();

        // Assert: 콜백 미호출
        await Task.Delay(300);
        expired.Should().BeFalse("Cancelled grace period should not fire callback");
    }

    [Fact]
    public async Task StartGracePeriod_CalledTwice_ShouldCancelFirst()
    {
        // Arrange
        using var actor = CreateActor();
        var firstExpired = false;
        var secondExpired = new TaskCompletionSource<bool>();

        // Act: 두 번 호출 — 첫 번째는 취소되어야 함
        actor.StartGracePeriod(TimeSpan.FromMilliseconds(100), () =>
        {
            firstExpired = true;
            return Task.CompletedTask;
        });

        actor.StartGracePeriod(TimeSpan.FromMilliseconds(100), () =>
        {
            secondExpired.SetResult(true);
            return Task.CompletedTask;
        });

        // Assert
        await Task.WhenAny(secondExpired.Task, Task.Delay(5000));
        firstExpired.Should().BeFalse("First grace period should be cancelled");
        (await secondExpired.Task).Should().BeTrue("Second grace period should fire");
    }

    [Fact]
    public async Task Dispose_ShouldCancelGracePeriod()
    {
        // Arrange
        var expired = false;
        var actor = CreateActor();

        actor.StartGracePeriod(TimeSpan.FromMilliseconds(100), () =>
        {
            expired = true;
            return Task.CompletedTask;
        });

        // Act: Dispose가 grace period를 취소해야 함
        actor.Dispose();

        // Assert
        await Task.Delay(300);
        expired.Should().BeFalse("Disposed actor should not fire grace period callback");
    }
}
