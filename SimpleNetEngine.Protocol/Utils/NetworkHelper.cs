using System.Net;
using System.Net.Sockets;

namespace SimpleNetEngine.Protocol.Utils;

public static class NetworkHelper
{
    public static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint?.Address.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}