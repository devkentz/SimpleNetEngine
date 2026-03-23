using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Middleware;

namespace SimpleNetEngine.Tests.Game.Core;

/// <summary>
/// PacketContext Scoped DI 주입 + SendNtf 자동화 테스트
/// </summary>
public class PacketContextScopedDiTests
{
    /// <summary>
    /// PacketContext가 Scoped DI에 등록되어 Controller에서 주입받을 수 있는지 검증
    /// </summary>
    [Fact]
    public async Task ProcessMessage_ShouldRegisterPacketContextInScopedDi()
    {
        // Arrange
        PacketContext? resolvedContext = null;

        var dispatcher = new Mock<IMessageDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IServiceProvider>(), It.IsAny<ISessionActor>(), It.IsAny<IActorMessage>()))
            .Returns<IServiceProvider, ISessionActor, IActorMessage>((sp, actor, msg) =>
            {
                // Dispatcher 내부에서 Scoped DI로 PacketContext를 resolve
                resolvedContext = sp.GetService<PacketContext>();
                return Task.FromResult<Response?>(null);
            });

        using var actor = CreateActorWithDispatcher(dispatcher.Object);

        var context = CreateTestContext();

        // Act
        actor.Push(new PacketActorMessage(context));
        await WaitForMailboxDrain(actor);

        // Assert
        resolvedContext.Should().NotBeNull("PacketContext should be registered in Scoped DI");
        resolvedContext.Should().BeSameAs(context, "resolved context should be the same instance");
    }

    /// <summary>
    /// SendNtf 호출 시 SequenceId가 자동 증가하는지 검증
    /// </summary>
    [Fact]
    public void SendNtf_ShouldAutoIncrementSequenceId()
    {
        // Arrange
        var sentSequenceIds = new List<ushort>();
        var context = CreateTestContext();
        context.SendResponse = (gw, sess, res, reqId, seqId) =>
        {
            sentSequenceIds.Add(seqId);
        };

        var mockActor = new Mock<ISessionActor>();
        ushort seqCounter = 100;
        mockActor.Setup(a => a.NextSequenceId()).Returns(() => ++seqCounter);
        mockActor.Setup(a => a.GatewayNodeId).Returns(1);
        mockActor.Setup(a => a.ActorId).Returns(999);

        context.Actor = mockActor.Object;

        // Act: SendNtf를 여러 번 호출
        context.SendNtf(Response.Ntf(null!));
        context.SendNtf(Response.Ntf(null!));
        context.SendNtf(Response.Ntf(null!));

        // Assert: 각 호출마다 SequenceId가 자동 증가
        sentSequenceIds.Should().HaveCount(3);
        sentSequenceIds[0].Should().Be(101);
        sentSequenceIds[1].Should().Be(102);
        sentSequenceIds[2].Should().Be(103);
    }

    /// <summary>
    /// SendNtf는 RequestId=0으로 전송되어야 함 (Notification은 요청에 대한 응답이 아님)
    /// </summary>
    [Fact]
    public void SendNtf_ShouldSendWithRequestIdZero()
    {
        // Arrange
        ushort capturedRequestId = 999;
        var context = CreateTestContext();
        context.RequestId = 42; // 현재 요청의 RequestId
        context.SendResponse = (gw, sess, res, reqId, seqId) =>
        {
            capturedRequestId = reqId;
        };

        var mockActor = new Mock<ISessionActor>();
        ushort seqCounter = 0;
        mockActor.Setup(a => a.NextSequenceId()).Returns(() => ++seqCounter);
        mockActor.Setup(a => a.GatewayNodeId).Returns(1);
        mockActor.Setup(a => a.ActorId).Returns(999);

        context.Actor = mockActor.Object;

        // Act
        context.SendNtf(Response.Ntf(null!));

        // Assert: Ntf는 RequestId=0
        capturedRequestId.Should().Be(0);
    }

    /// <summary>
    /// SendNtf는 Actor의 GatewayNodeId와 SessionId를 사용해야 함
    /// </summary>
    [Fact]
    public void SendNtf_ShouldUseActorRoutingInfo()
    {
        // Arrange
        long capturedGateway = 0;
        long capturedSession = 0;
        var context = CreateTestContext();
        context.GatewayNodeId = 100;
        context.SessionId = 200;
        context.SendResponse = (gw, sess, res, reqId, seqId) =>
        {
            capturedGateway = gw;
            capturedSession = sess;
        };

        var mockActor = new Mock<ISessionActor>();
        ushort seqCounter = 0;
        mockActor.Setup(a => a.NextSequenceId()).Returns(() => ++seqCounter);
        mockActor.Setup(a => a.GatewayNodeId).Returns(100);
        mockActor.Setup(a => a.ActorId).Returns(200);

        context.Actor = mockActor.Object;

        // Act
        context.SendNtf(Response.Ntf(null!));

        // Assert
        capturedGateway.Should().Be(100);
        capturedSession.Should().Be(200);
    }

    /// <summary>
    /// Actor 설정 없이 SendNtf 호출 시 예외 발생
    /// </summary>
    [Fact]
    public void SendNtf_WithoutActor_ShouldThrow()
    {
        // Arrange
        var context = CreateTestContext();
        context.SendResponse = (gw, sess, res, reqId, seqId) => { };

        // Act & Assert
        var act = () => context.SendNtf(Response.Ntf(null!));
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// SendResponse 콜백 없이 SendNtf 호출 시 예외 발생
    /// </summary>
    [Fact]
    public void SendNtf_WithoutSendResponseCallback_ShouldThrow()
    {
        // Arrange
        var context = CreateTestContext();
        context.SendResponse = null;

        var mockActor = new Mock<ISessionActor>();
        context.Actor = mockActor.Object;

        // Act & Assert
        var act = () => context.SendNtf(Response.Ntf(null!));
        act.Should().Throw<InvalidOperationException>();
    }

    #region Helpers

    private static PacketContext CreateTestContext()
    {
        return new PacketContext
        {
            GatewayNodeId = 1,
            SessionId = 100,
            RequestId = 1,
            SequenceId = 0,
            Payload = ReadOnlyMemory<byte>.Empty
        };
    }

    private static SessionActor CreateActorWithDispatcher(IMessageDispatcher dispatcher)
    {
        var services = new ServiceCollection();
        // PacketContext Scoped DI 등록 (GameServiceExtensions와 동일한 패턴)
        services.AddScoped<PacketContextHolder>();
        services.AddScoped(sp =>
            sp.GetRequiredService<PacketContextHolder>().Context
            ?? throw new InvalidOperationException("PacketContext is not available."));
        var sp = services.BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        return new SessionActor(
            actorId: 1000,
            userId: 42,
            gatewayNodeId: 200,
            scopeFactory: scopeFactory,
            dispatcher: dispatcher,
            pipeline: new MiddlewarePipelineFactory(Enumerable.Empty<IPacketMiddleware>()).CreateDefaultPipeline(),
            logger: Mock.Of<ILogger>());
    }

    private static async Task WaitForMailboxDrain(SessionActor actor)
    {
        // Mailbox가 비워질 때까지 짧은 대기
        await Task.Delay(200);
    }

    #endregion
}
