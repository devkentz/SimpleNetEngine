using Google.Protobuf;
using SimpleNetEngine.Infrastructure.Middleware;
using SimpleNetEngine.Protocol.Packets;

namespace NodeSample.Middleware
{
    internal class SampleNodeMiddleware : INodeMiddleware
    {
        private readonly ILogger<SampleNodeMiddleware> _logger;

        public SampleNodeMiddleware(ILogger<SampleNodeMiddleware> logger)
        {
            _logger = logger;
        }

        public Task<IMessage?> InvokeAsync(NodePacket packet, Func<NodePacket, Task<IMessage?>> next)
        {
            _logger.LogInformation("SampleNodeMiddleware: Processing MsgId={MsgId} Src={SrcId} Dest{DestId}", packet.Header.MsgId, packet.Header.Source, packet.Header.Dest);
            return next(packet);
        }
    }
}