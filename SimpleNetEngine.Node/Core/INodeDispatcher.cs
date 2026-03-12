using Google.Protobuf;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Node.Core;

/// <summary>
/// Node Controller 디스패처 인터페이스
/// Service Mesh에서 수신된 InternalPacket을 NodeController 핸들러로 라우팅
/// </summary>
public interface INodeDispatcher
{
    void RegisterHandler(int msgId, Func<IServiceProvider, NodePacket, Task<IMessage?>> handler);
    Task<IMessage?> DispatchAsync(IServiceProvider scope, NodePacket packet);
    bool HasHandler(int msgId);
}

/// <summary>
/// NodeDispatcher 핸들러 등록을 DI로 위임하기 위한 인터페이스.
/// Source Generator가 구현체를 자동 생성하여 DI에 등록하면,
/// AddNodeControllers()에서 auto-discover하여 핸들러를 등록한다.
/// </summary>
public interface INodeHandlerRegistrar
{
    void RegisterHandlers(NodeDispatcher dispatcher);
}
