namespace SimpleNetEngine.Node.Core;

public static class NodeMetadataKeys
{
    /// <summary>
    /// GameSessionChannel 수신 포트 (Gateway → GameServer 방향, GameServer가 Bind)
    /// </summary>
    public const string GameSessionChannelPort = "GameSessionChannel_Port";

    /// <summary>
    /// GameSessionChannel 송신 포트 (GameServer → Gateway 방향, GameServer가 Bind)
    /// </summary>
    public const string GameSessionChannelSendPort = "GameSessionChannel_SendPort";

    public const string GatewayClientTcpPort = "Gateway_ClientTcpPort";
}
