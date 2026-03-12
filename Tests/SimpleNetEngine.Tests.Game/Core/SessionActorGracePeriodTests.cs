using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Middleware;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// SessionActor.MarkDisconnected / ClearDisconnected 유닛 테스트
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
    public void MarkDisconnected_ShouldSetTimestamp()
    {
        // Arrange
        using var actor = CreateActor();
        actor.DisconnectedTicks.Should().Be(0);

        // Act
        actor.MarkDisconnected();

        // Assert
        actor.DisconnectedTicks.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ClearDisconnected_ShouldResetTimestamp()
    {
        // Arrange
        using var actor = CreateActor();
        actor.MarkDisconnected();
        actor.DisconnectedTicks.Should().BeGreaterThan(0);

        // Act
        actor.ClearDisconnected();

        // Assert
        actor.DisconnectedTicks.Should().Be(0);
    }

    [Fact]
    public void MarkDisconnected_ShouldRecordApproximateTime()
    {
        // Arrange
        using var actor = CreateActor();
        var before = Stopwatch.GetTimestamp();

        // Act
        actor.MarkDisconnected();

        // Assert: 타임스탬프가 현재 시간 근방
        var after = Stopwatch.GetTimestamp();
        actor.DisconnectedTicks.Should().BeInRange(before, after);
    }
}
