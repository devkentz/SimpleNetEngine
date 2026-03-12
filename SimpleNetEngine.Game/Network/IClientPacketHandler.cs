using SimpleNetEngine.Game.Middleware;

namespace SimpleNetEngine.Game.Network;

/// <summary>
/// 클라이언트 패킷 처리 인터페이스
/// P2P Gateway Listener가 수신한 클라이언트 패킷을 처리하는 핸들러
/// </summary>
public interface IClientPacketHandler
{
    /// <summary>
    /// 클라이언트 패킷을 비동기로 처리
    /// </summary>
    /// <param name="context">패킷 처리 컨텍스트</param>
    void HandlePacket(PacketContext context);
}
