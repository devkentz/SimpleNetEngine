using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetMQ;
using SimpleNetEngine.Node.Core;
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
        msg.InitPool(NodeHeader.SizeOf);
        MemoryMarshal.Write(msg.Slice(), in header);
        return NodePacket.Create(ref msg);
    }

    [Fact(DisplayName = "Sequential 모드: 이벤트 루프에서 await interleaving으로 동작해야 함")]
    public async Task SequentialMode_Should_Execute_On_EventLoop()
    {
        var handler = new SequentialTestHandler(_mockLogger.Object);
        int requestCount = 5;

        for (int i = 0; i < requestCount; i++) handler.ProcessPacket(CreateTestPacket(i));

        var sw = Stopwatch.StartNew();
        while (handler.ProcessedIds.Count < requestCount && sw.ElapsedMilliseconds < 2000) await Task.Delay(10);

        handler.ProcessedIds.Count.Should().Be(requestCount);
    }

    [Fact(DisplayName = "Parallel 모드: 모든 패킷이 병렬로 실행되어야 함")]
    public async Task ParallelMode_Should_Execute_All_At_Once()
    {
        var handler = new ParallelTestHandler(_mockLogger.Object);
        int requestCount = 5;

        for (int i = 0; i < requestCount; i++) handler.ProcessPacket(CreateTestPacket(i));

        var sw = Stopwatch.StartNew();
        while (handler.ProcessedIds.Count < requestCount && sw.ElapsedMilliseconds < 2000) await Task.Delay(10);

        handler.MaxConcurrentCount.Should().BeGreaterThan(1);
    }

    [Fact(DisplayName = "Sequential 모드: 예외 발생 시에도 이벤트 루프가 복구되어 다음 메시지를 처리해야 함")]
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
