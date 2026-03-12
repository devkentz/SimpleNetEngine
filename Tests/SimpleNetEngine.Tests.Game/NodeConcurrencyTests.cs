using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetMQ;
using SimpleNetEngine.Node.Core;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Tests.Game.Node;

public class NodeConcurrencyTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    #region 테스트용 핸들러 정의

    private class SequentialTestHandler : SequentialNodeEventHandler
    {
        public ConcurrentQueue<int> ProcessedIds { get; } = new();
        public int ActiveCount = 0;
        public int MaxConcurrentCount = 0;

        public SequentialTestHandler(ILogger logger) : base(logger) { }

        protected override async Task ProcessPacketInternalAsync(NodePacket packet)
        {
            var id = packet.Header.MsgId;
            var current = Interlocked.Increment(ref ActiveCount);
            lock (this) { MaxConcurrentCount = Math.Max(MaxConcurrentCount, current); }

            await Task.Delay(100);

            ProcessedIds.Enqueue(id);
            Interlocked.Decrement(ref ActiveCount);
        }
    }

    private class ParallelTestHandler : ParallelNodeEventHandler
    {
        public ConcurrentQueue<int> ProcessedIds { get; } = new();
        public int ActiveCount = 0;
        public int MaxConcurrentCount = 0;

        public ParallelTestHandler(ILogger logger) : base(logger) { }

        protected override async Task ProcessPacketInternalAsync(NodePacket packet)
        {
            var id = packet.Header.MsgId;
            var current = Interlocked.Increment(ref ActiveCount);
            lock (this) { MaxConcurrentCount = Math.Max(MaxConcurrentCount, current); }

            await Task.Delay(100);

            ProcessedIds.Enqueue(id);
            Interlocked.Decrement(ref ActiveCount);
        }
    }

    /// <summary>
    /// 테스트용 INodeActor — 자체 QueuedResponseWriter 메일박스 소유
    /// </summary>
    private class TestNodeActor : INodeActor
    {
        private readonly QueuedResponseWriter<NodePacket> _mailbox;
        private readonly ConcurrentQueue<int> _processedIds;
        private readonly Action _onProcess;

        public long ActorId { get; }

        public TestNodeActor(long actorId, ConcurrentQueue<int> processedIds, Counter counter, ILogger logger)
        {
            ActorId = actorId;
            _processedIds = processedIds;
            _onProcess = () =>
            {
                var current = Interlocked.Increment(ref counter.ActiveCount);
                lock (counter) { counter.MaxConcurrentCount = Math.Max(counter.MaxConcurrentCount, current); }
            };
            _mailbox = new QueuedResponseWriter<NodePacket>(ProcessAsync, logger);
        }

        public void Push(NodePacket packet) => _mailbox.Write(packet);

        private async Task ProcessAsync(NodePacket packet)
        {
            var id = packet.Header.MsgId;
            _onProcess();

            await Task.Delay(100);

            _processedIds.Enqueue(id);
            Interlocked.Decrement(ref ((Counter)_onProcess.Target!).ActiveCount);
        }
    }

    /// <summary>
    /// ActorNodeEventHandler 테스트용 구체 클래스
    /// </summary>
    private class ActorSerializedTestHandler : ActorNodeEventHandler
    {
        public ActorSerializedTestHandler(ILogger logger, INodeActorManager actorManager)
            : base(logger, actorManager) { }
    }

    /// <summary>
    /// 동시성 카운터 (테스트 공유)
    /// </summary>
    private class Counter
    {
        public int ActiveCount = 0;
        public int MaxConcurrentCount = 0;
    }

    private class SequentialExceptionTestHandler : SequentialNodeEventHandler
    {
        public ConcurrentQueue<int> ProcessedIds { get; } = new();

        public SequentialExceptionTestHandler(ILogger logger) : base(logger) { }

        protected override async Task ProcessPacketInternalAsync(NodePacket packet)
        {
            if (packet.Header.MsgId == 1) throw new Exception("Test Exception");

            await Task.Delay(10);
            ProcessedIds.Enqueue(packet.Header.MsgId);
        }
    }

    #endregion

    private NodePacket CreateTestPacket(int msgId, long actorId = 0)
    {
        var header = new NodeHeader { MsgId = msgId, ActorId = actorId };
        var msg = new Msg();
        msg.InitPool(NodeHeader.Size);
        MemoryMarshal.Write(msg.Slice(), in header);
        return NodePacket.Create(ref msg);
    }

    [Fact(DisplayName = "Sequential 모드: 모든 요청은 하나씩 순차적으로 실행되어야 함")]
    public async Task SequentialMode_Should_Execute_One_At_A_Time()
    {
        var handler = new SequentialTestHandler(_mockLogger.Object);
        int requestCount = 5;

        for (int i = 0; i < requestCount; i++) handler.ProcessPacket(CreateTestPacket(i));

        var sw = Stopwatch.StartNew();
        while (handler.ProcessedIds.Count < requestCount && sw.ElapsedMilliseconds < 2000) await Task.Delay(10);

        handler.ProcessedIds.Count.Should().Be(requestCount);
        handler.MaxConcurrentCount.Should().Be(1);
    }

    [Fact(DisplayName = "Parallel 모드: ActorId=0이면 즉시 병렬로 실행되어야 함")]
    public async Task ParallelMode_Should_Execute_All_At_Once()
    {
        var handler = new ParallelTestHandler(_mockLogger.Object);
        int requestCount = 5;

        for (int i = 0; i < requestCount; i++) handler.ProcessPacket(CreateTestPacket(i));

        var sw = Stopwatch.StartNew();
        while (handler.ProcessedIds.Count < requestCount && sw.ElapsedMilliseconds < 2000) await Task.Delay(10);

        handler.MaxConcurrentCount.Should().BeGreaterThan(1);
    }

    [Fact(DisplayName = "ActorSerialized 모드: 서로 다른 ActorId는 병렬로 실행되어야 함")]
    public async Task ActorSerialized_DifferentActors_Should_Execute_In_Parallel()
    {
        var processedIds = new ConcurrentQueue<int>();
        var counter = new Counter();
        var actorManager = new NodeActorManager();

        // 서로 다른 ActorId로 actor 2개 등록
        actorManager.AddActor(new TestNodeActor(1, processedIds, counter, _mockLogger.Object));
        actorManager.AddActor(new TestNodeActor(2, processedIds, counter, _mockLogger.Object));

        var handler = new ActorSerializedTestHandler(_mockLogger.Object, actorManager);

        handler.ProcessPacket(CreateTestPacket(1, actorId: 1));
        handler.ProcessPacket(CreateTestPacket(2, actorId: 2));

        var sw = Stopwatch.StartNew();
        while (processedIds.Count < 2 && sw.ElapsedMilliseconds < 1000) await Task.Delay(10);

        counter.MaxConcurrentCount.Should().Be(2, "서로 다른 액터는 병렬 처리가 가능해야 함");
    }

    [Fact(DisplayName = "Parallel 모드: 같은 ActorId(ServerId)는 순차 처리되어야 함")]
    public async Task ParallelMode_SameActorId_Should_Execute_Sequentially()
    {
        var handler = new ParallelTestHandler(_mockLogger.Object);

        for (int i = 0; i < 3; i++) handler.ProcessPacket(CreateTestPacket(i, actorId: 100));

        var sw = Stopwatch.StartNew();
        while (handler.ProcessedIds.Count < 3 && sw.ElapsedMilliseconds < 2000) await Task.Delay(10);

        handler.ProcessedIds.Count.Should().Be(3);
        handler.MaxConcurrentCount.Should().Be(1, "같은 ActorId는 순차 처리되어야 함");
    }

    [Fact(DisplayName = "Parallel 모드: 다른 ActorId(ServerId)는 병렬로 실행되어야 함")]
    public async Task ParallelMode_DifferentActorId_Should_Execute_In_Parallel()
    {
        var handler = new ParallelTestHandler(_mockLogger.Object);

        handler.ProcessPacket(CreateTestPacket(1, actorId: 100));
        handler.ProcessPacket(CreateTestPacket(2, actorId: 200));

        var sw = Stopwatch.StartNew();
        while (handler.ProcessedIds.Count < 2 && sw.ElapsedMilliseconds < 1000) await Task.Delay(10);

        handler.MaxConcurrentCount.Should().Be(2, "다른 ActorId(ServerId)는 병렬 처리가 가능해야 함");
    }

    [Fact(DisplayName = "ActorSerialized 모드: 존재하지 않는 Actor에 패킷 전송 시 OnActorNotFound 호출")]
    public void ActorSerialized_UnknownActorId_Should_Call_OnActorNotFound()
    {
        var actorManager = new NodeActorManager();
        var handler = new ActorSerializedTestHandler(_mockLogger.Object, actorManager);

        // ActorId=999는 등록되지 않음 — OnActorNotFound에서 경고 로그 + 패킷 해제
        handler.ProcessPacket(CreateTestPacket(1, actorId: 999));

        // 예외 없이 정상 완료되어야 함 (OnActorNotFound 기본 구현이 처리)
    }

    [Fact(DisplayName = "Sequential 모드: 예외 발생 시에도 큐가 복구되어 다음 메시지를 처리해야 함")]
    public async Task SequentialMode_Exception_Should_Not_Block_Queue()
    {
        var handler = new SequentialExceptionTestHandler(_mockLogger.Object);

        handler.ProcessPacket(CreateTestPacket(1)); // Throws
        handler.ProcessPacket(CreateTestPacket(2)); // Success

        var sw = Stopwatch.StartNew();
        while (handler.ProcessedIds.Count < 1 && sw.ElapsedMilliseconds < 1000) await Task.Delay(10);

        handler.ProcessedIds.Should().Contain(2, "첫 번째 요청의 실패가 두 번째 요청에 영향을 주면 안 됨");
    }
}
