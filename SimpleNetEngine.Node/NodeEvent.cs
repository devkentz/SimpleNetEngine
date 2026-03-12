using Google.Protobuf;
using SimpleNetEngine.Node.Network;

namespace SimpleNetEngine.Node
{
    public abstract class NodeEvent<T> where T : IMessage<T>, new()
    {
        public abstract void Startup();
        public abstract void Shutdown();

        public abstract void CreateActor(T createPacket);
        public abstract void RemoveActor();
        
        public virtual void RemovedNode(RemoteNode remoteNode){}
        public virtual void NewNode(RemoteNode remoteNode){}
        
    }
}