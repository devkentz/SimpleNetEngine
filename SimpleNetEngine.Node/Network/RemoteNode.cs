using Internal.Protocol;

namespace SimpleNetEngine.Node.Network;

public class RemoteNode(ServerInfo serverInfo)
{
    public ServerInfo ServerInfo => serverInfo;
    public EServerType ServerType => ServerInfo.Type;
    public long Identity => ServerInfo.RemoteId;
    public byte[] IdentityBytes { get; } = serverInfo.IdentityBytes.Span.ToArray();
    public string Address => serverInfo.Address;
    public int Port => serverInfo.Port;
    public string ServerTypeString => ServerType switch
    {
        EServerType.Gateway => "Gateway",
        EServerType.Game => "Game",
        EServerType.Api => string.IsNullOrEmpty(ServerInfo.Name) ? "Unknown" : ServerInfo.Name,
        _ => "Unknown"
    };

    public void ConnectionClosed() => IsClose =  true;

    public bool IsClose { get; private set; }
}