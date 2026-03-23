using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// 메시지 디스패처 인터페이스
/// Actor 내부에서 메시지를 실제 비즈니스 로직 핸들러로 라우팅
/// TcpServer의 MessageHandler 패턴을 GameServer P2P 환경에 맞게 적응
/// </summary>
public interface IMessageDispatcher
{
    /// <summary>
    /// 메시지 처리 (Actor mailbox 컨슈머에서 호출)
    /// </summary>
    /// <param name="serviceProvider">Scoped DI 컨테이너</param>
    /// <param name="actor">메시지를 처리하는 Actor</param>
    /// <param name="message">처리할 메시지</param>
    /// <returns>Response (null이면 응답 없음, Zero-Copy 직렬화는 SendResponse에서 수행)</returns>
    Task<Response?> DispatchAsync(IServiceProvider serviceProvider, ISessionActor actor, IActorMessage message);
}

/// <summary>
/// 기본 메시지 디스패처 구현
/// Opcode(MsgId) 기반으로 등록된 핸들러에 메시지 라우팅
/// RequireActorState 어트리뷰트 기반 Actor 상태 접근 제어 포함
/// </summary>
public class MessageDispatcher(ILogger<MessageDispatcher> logger) : IMessageDispatcher
{
    private readonly Dictionary<int, Func<IServiceProvider, ISessionActor, ReadOnlyMemory<byte>, Task<Response?>>> _handlers = [];

    /// <summary>
    /// MsgId별 RequireActorState 캐시 (기동 시 1회 설정 후 읽기 전용)
    /// </summary>
    private readonly Dictionary<int, ActorState[]> _stateCache = [];

    /// <summary>
    /// 핸들러 등록
    /// </summary>
    public void RegisterHandler(
        int opcode,
        Func<IServiceProvider, ISessionActor, ReadOnlyMemory<byte>, Task<Response?>> handler,
        ActorState[]? allowedStates = null)
    {
        _handlers[opcode] = handler;
        _stateCache[opcode] = allowedStates ?? [];
    }

    /// <summary>
    /// 기본 핸들러 등록 (opcode 매칭 실패 시 fallback)
    /// </summary>
    public Func<IServiceProvider, ISessionActor, ReadOnlyMemory<byte>, Task<Response?>>? DefaultHandler { get; set; }

    public async Task<Response?> DispatchAsync(
        IServiceProvider serviceProvider,
        ISessionActor actor,
        IActorMessage message)
    {
        // Payload에서 opcode 추출 시도
        
        var opcode = ExtractOpcode(message.Payload);
        if (opcode < 0)
            return null;

        if (_handlers.TryGetValue(opcode, out var handler))
        {
            // ★ RequireActorState 상태 검사 (아키텍처 스펙: 상태 기반 패킷 접근 제어)
            if (_stateCache.TryGetValue(opcode, out var allowedStates) && allowedStates.Length > 0)
            {
                if (!Array.Exists(allowedStates, s => s == actor.Status))
                {
                    Log.ActorStateRejected(logger, actor.ActorId, opcode, actor.Status);
                    return Response.Error((short)ErrorCode.GameActorInvalidState);
                }
            }
            

            return await handler(serviceProvider, actor, message.Payload);
        }

        if (DefaultHandler != null)
        {
            return await DefaultHandler(serviceProvider, actor, message.Payload);
        }

        return null;
    }

    /// <summary>
    /// Payload에서 opcode 추출
    /// Wire format: [EndPointHeader(8)][GameHeader(MsgId(4)+SequenceId(2)+RequestId(2))][Proto]
    /// </summary>
    private static int ExtractOpcode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < EndPointHeader.SizeOf + sizeof(int))
            return -1;

        return BinaryPrimitives.ReadInt32LittleEndian(payload.Span[EndPointHeader.SizeOf..]);
    }
}

/// <summary>
/// MessageDispatcher 핸들러 등록을 DI로 위임하기 위한 인터페이스.
/// Source Generator가 구현체를 자동 생성하여 DI에 등록하면,
/// AddUserControllers()에서 auto-discover하여 핸들러를 등록한다.
/// </summary>
public interface IUserHandlerRegistrar
{
    void RegisterHandlers(MessageDispatcher dispatcher);
}

