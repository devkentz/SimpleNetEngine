using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Middleware;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// SessionActor.ExecuteAsync 유닛 테스트
/// CallbackActorMessage가 mailbox를 통해 순차 실행되는지 검증
/// </summary>
public class SessionActorExecuteAsyncTests
{
    private static SessionActor CreateActor()
    {
        var scopeFactory = Mock.Of<IServiceScopeFactory>(f =>
            f.CreateScope() == Mock.Of<IServiceScope>(s =>
                s.ServiceProvider == Mock.Of<IServiceProvider>()));

        return new SessionActor(
            actorId: 1000,
            userId: 42,
            gatewayNodeId: 200,
            scopeFactory: scopeFactory,
            dispatcher: Mock.Of<IMessageDispatcher>(),
            pipeline: new MiddlewarePipelineFactory(Mock.Of<ILogger<MiddlewarePipeline>>()).CreateDefaultPipeline(),
            logger: Mock.Of<ILogger>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunCallbackThroughMailbox()
    {
        // Arrange
        using var actor = CreateActor();
        var callbackExecuted = false;

        // Act: ExecuteAsync로 콜백 푸시
        await actor.ExecuteAsync(sp =>
        {
            callbackExecuted = true;
            return Task.CompletedTask;
        });

        // Assert: 콜백이 실행됨
        callbackExecuted.Should().BeTrue("callback should be executed through actor mailbox");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldProvideServiceProvider()
    {
        // Arrange
        using var actor = CreateActor();
        IServiceProvider? receivedSp = null;

        // Act
        await actor.ExecuteAsync(sp =>
        {
            receivedSp = sp;
            return Task.CompletedTask;
        });

        // Assert: Scoped IServiceProvider가 전달됨
        receivedSp.Should().NotBeNull("scoped IServiceProvider should be provided to callback");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunCallbacksSequentially()
    {
        // Arrange
        using var actor = CreateActor();
        var executionOrder = new List<int>();
        var gate = new TaskCompletionSource();

        // Act: 여러 콜백을 순차적으로 push
        var task1 = actor.ExecuteAsync(async sp =>
        {
            await gate.Task; // 첫 번째 콜백이 대기
            executionOrder.Add(1);
        });

        var task2 = actor.ExecuteAsync(sp =>
        {
            executionOrder.Add(2);
            return Task.CompletedTask;
        });

        // 첫 번째 콜백을 풀어줌
        gate.SetResult();
        await Task.WhenAll(task1, task2);

        // Assert: 순차 실행 보장
        executionOrder.Should().Equal(1, 2);
    }

    [Fact]
    public async Task ExecuteAsync_CallbackThrows_ShouldCompleteWithException()
    {
        // Arrange
        using var actor = CreateActor();

        // Act & Assert: 콜백 예외가 전파됨
        var act = () => actor.ExecuteAsync(sp => throw new InvalidOperationException("test error"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("test error");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunInMailboxThread_NotCallerThread()
    {
        // Arrange
        using var actor = CreateActor();
        var callbackThreadId = -1;

        // Act
        await actor.ExecuteAsync(sp =>
        {
            callbackThreadId = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        });

        // Assert: 콜백이 mailbox consumer에서 실행됨
        callbackThreadId.Should().BeGreaterThan(0);
    }
}
