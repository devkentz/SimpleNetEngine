using SimpleNetEngine.Game.Options;
using Internal.Protocol;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Protocol.Packets;
using Microsoft.Extensions.Options;
using SimpleNetEngine.Node.Network;

namespace SimpleNetEngine.Game.Network;

/// <summary>
/// Kickout 요청 발신 클라이언트
/// GameServerHub에서 Cross-Node Duplicate Login 시 사용
///
/// 역할:
/// - 다른 GameServer에게 KickoutRequest 발신 (SendKickoutRequestAsync)
/// - INodeSender의 내장 RequestCache(TaskCompletionSource)를 활용하여 응답을 대기
///
/// 참고: Kickout 수신(인바운드) 처리는 SessionController가 담당
/// </summary>
public class KickoutMessageHandler : IDisposable
{
    private readonly ILogger<KickoutMessageHandler> _logger;
    private readonly GameOptions _options;
    private readonly INodeSender _nodeSender;

    private static readonly TimeSpan KickoutTimeout = TimeSpan.FromSeconds(5);

    public KickoutMessageHandler(
        ILogger<KickoutMessageHandler> logger,
        IOptions<GameOptions> options,
        INodeSender nodeSender)
    {
        _logger = logger;
        _options = options.Value;
        _nodeSender = nodeSender;
    }

    /// <summary>
    /// 다른 GameServer에게 Kickout 요청 전송 (비동기 응답 대기)
    /// </summary>
    public virtual async Task<ServiceMeshKickoutRes> SendKickoutRequestAsync(
        long targetNodeId,
        long userId,
        long sessionId,
        long gatewayNodeId)
    {
        var request = new ServiceMeshKickoutReq
        {
            UserId = userId,
            SessionId = sessionId,
            GatewayNodeId = gatewayNodeId
        };

        try
        {
            _logger.LogInformation(
                "Sending KickoutRequest to GameServer-{NodeId}: UserId={UserId}",
                targetNodeId, userId);

            // INodeSender.RequestAsync가 내부적으로 requestKey 할당 및 TaskCompletionSource 매핑을 처리함
            // 타임아웃은 NodeConfig.RequestTimeoutMs 설정값에 따라 내부적으로 자동 처리됨
            return await _nodeSender.RequestAsync<ServiceMeshKickoutReq, ServiceMeshKickoutRes>(
                NodePacket.ServerActorId,
                targetNodeId,
                request);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "KickoutRequest timeout: UserId={UserId}, TargetNodeId={TargetNodeId}",
                userId, targetNodeId);

            return new ServiceMeshKickoutRes
            {
                UserId = userId,
                Success = false,
                ErrorCode = ServiceMeshKickoutErrorCode.InternalError
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send KickoutRequest: UserId={UserId}", userId);
            
            return new ServiceMeshKickoutRes
            {
                UserId = userId,
                Success = false,
                ErrorCode = ServiceMeshKickoutErrorCode.InternalError
            };
        }
    }

    public void Dispose()
    {
        // 구독 해제할 이벤트가 더 이상 없으므로 비워둠
    }
}
