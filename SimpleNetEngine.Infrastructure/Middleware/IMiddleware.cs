using Google.Protobuf;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Infrastructure.Middleware;

/// <summary>
/// Node Service Mesh용 미들웨어 인터페이스 (노드간 통신)
/// </summary>
public interface INodeMiddleware
{
    Task<IMessage?> InvokeAsync(NodePacket packet, Func<NodePacket, Task<IMessage?>> next);
}

/// <summary>
/// GameSessionChannel용 미들웨어 인터페이스 (클라이언트-서버 통신)
/// </summary>
public interface IUserMiddleware
{
    // Context 정보는 각 레이어의 요구사항에 맞춰 확장 가능
    Task InvokeAsync(object context, Func<Task> next);
}
