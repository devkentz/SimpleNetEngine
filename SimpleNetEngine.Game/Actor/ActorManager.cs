using System.Collections.Concurrent;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Protocol.Packets;
using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// 게임 세션 Actor 관리 인터페이스
/// SessionId -> ISessionActor 매핑 관리
/// Result 패턴으로 명시적 에러 처리
/// </summary>
public interface ISessionActorManager
{
    /// <summary>
    /// SessionId로 Actor 조회
    /// </summary>
    Result<ISessionActor> GetActor(long sessionId);

    /// <summary>
    /// UserId로 Actor 조회
    /// </summary>
    Result<ISessionActor> GetActorByUserId(long userId);

    /// <summary>
    /// ReconnectKey로 Actor 조회 (재접속 시 사용)
    /// </summary>
    Result<ISessionActor> GetActorByReconnectKey(Guid reconnectKey);

    /// <summary>
    /// Actor 등록
    /// 성공: 등록된 Actor 반환
    /// 실패: ActorAddFailed 에러 (중복 등)
    /// </summary>
    Result<ISessionActor> TryAddActor(ISessionActor actor);

    /// <summary>
    /// Actor 제거 및 Dispose (외부 스레드에서 호출 전용)
    /// Actor mailbox 내부에서 호출하면 self-deadlock 발생 → UnregisterActor 사용
    /// 성공: Result.Ok()
    /// 실패: ActorNotFound 에러
    /// </summary>
    Result RemoveActor(long sessionId);

    /// <summary>
    /// Actor를 딕셔너리에서 제거하되 Dispose하지 않음
    /// Actor mailbox 내부에서 자기 자신을 정리할 때 사용 (Logout, GracePeriod 만료 등)
    /// Dispose는 ActorDisposeQueue를 통해 외부에서 처리
    /// 반환: 제거된 Actor (Dispose 책임은 호출자에게)
    /// </summary>
    Result<ISessionActor> UnregisterActor(long sessionId);

    /// <summary>
    /// Actor의 ReconnectKey 변경 시 역인덱스 갱신
    /// RegenerateReconnectKey() 호출 후 반드시 호출해야 함
    /// </summary>
    void UpdateReconnectKey(long sessionId, Guid oldKey, Guid newKey);

    /// <summary>
    /// Actor의 SessionId를 변경 (Same-Node Reconnect 시 사용)
    /// 기존 SessionId 매핑을 제거하고 새 SessionId로 재등록
    /// Actor는 Dispose되지 않음
    /// </summary>
    Result RekeyActor(long oldSessionId, long newSessionId);

    /// <summary>
    /// 모든 Actor 열거 (InactivityScanner 등에서 사용)
    /// </summary>
    IEnumerable<ISessionActor> GetAllActors();

    /// <summary>
    /// 활성 Actor 수
    /// </summary>
    int Count { get; }
}

/// <summary>
/// 게임 세션 Actor 관리자 구현
/// ConcurrentDictionary 기반 thread-safe 관리
/// SessionId -> Actor 매핑 + UserId -> SessionId 역인덱스
/// </summary>
public class SessionActorManager : ISessionActorManager
{
    private readonly ConcurrentDictionary<long, ISessionActor> _actorsBySessionId = new();
    private readonly ConcurrentDictionary<long, long> _sessionIdByUserId = new();
    private readonly ConcurrentDictionary<Guid, long> _sessionIdByReconnectKey = new();
    private readonly ILogger<SessionActorManager> _logger;

    public SessionActorManager(ILogger<SessionActorManager> logger)
    {
        _logger = logger;
    }

    public int Count => _actorsBySessionId.Count;

    public IEnumerable<ISessionActor> GetAllActors() => _actorsBySessionId.Values;

    public Result<ISessionActor> GetActor(long sessionId)
    {
        var actor = _actorsBySessionId.GetValueOrDefault(sessionId);

        return actor != null
            ? Result<ISessionActor>.Ok(actor)
            : Result<ISessionActor>.Failure(
                ErrorCode.GameActorNotFound,
                $"Actor not found for SessionId: {sessionId}");
    }

    public Result<ISessionActor> GetActorByUserId(long userId)
    {
        if (_sessionIdByUserId.TryGetValue(userId, out var sessionId))
        {
            var actor = _actorsBySessionId.GetValueOrDefault(sessionId);

            if (actor != null)
                return Result<ISessionActor>.Ok(actor);

            _logger.LogWarning(
                "Inconsistent state: UserId mapping exists but Actor not found. UserId={UserId}, SessionId={SessionId}",
                userId, sessionId);

            _sessionIdByUserId.TryRemove(new KeyValuePair<long, long>(userId, sessionId));
        }

        return Result<ISessionActor>.Failure(
            ErrorCode.GameActorNotFound,
            $"Actor not found for UserId: {userId}");
    }

    public Result<ISessionActor> GetActorByReconnectKey(Guid reconnectKey)
    {
        if (_sessionIdByReconnectKey.TryGetValue(reconnectKey, out var sessionId))
        {
            var actor = _actorsBySessionId.GetValueOrDefault(sessionId);

            if (actor != null)
                return Result<ISessionActor>.Ok(actor);

            _logger.LogWarning(
                "Inconsistent state: ReconnectKey mapping exists but Actor not found. ReconnectKey={ReconnectKey}, SessionId={SessionId}",
                reconnectKey, sessionId);

            _sessionIdByReconnectKey.TryRemove(new KeyValuePair<Guid, long>(reconnectKey, sessionId));
        }

        return Result<ISessionActor>.Failure(
            ErrorCode.GameActorNotFound,
            $"Actor not found for ReconnectKey: {reconnectKey}");
    }

    public Result<ISessionActor> TryAddActor(ISessionActor actor)
    {
        if (_actorsBySessionId.TryAdd(actor.ActorId, actor))
        {
            // UserId 역인덱스 갱신 (이전 매핑 덮어쓰기)
            _sessionIdByUserId[actor.UserId] = actor.ActorId;

            // ReconnectKey 역인덱스 갱신
            _sessionIdByReconnectKey[actor.ReconnectKey] = actor.ActorId;

            _logger.LogDebug(
                "Actor added: ActorId={ActorId}, UserId={UserId}, ReconnectKey={ReconnectKey}, Total={Count}",
                actor.ActorId, actor.UserId, actor.ReconnectKey, _actorsBySessionId.Count);

            return Result<ISessionActor>.Ok(actor);
        }

        _logger.LogWarning(
            "Actor already exists: ActorId={ActorId}, UserId={UserId}",
            actor.ActorId, actor.UserId);

        return Result<ISessionActor>.Failure(
            ErrorCode.GameActorAddFailed,
            $"Actor already exists for ActorId: {actor.ActorId}");
    }

    public Result RemoveActor(long sessionId)
    {
        if (_actorsBySessionId.TryRemove(sessionId, out var actor))
        {
            RemoveFromIndexes(actor, sessionId);
            actor.Dispose();

            _logger.LogDebug(
                "Actor removed: ActorId={ActorId}, UserId={UserId}, Remaining={Count}",
                sessionId, actor.UserId, _actorsBySessionId.Count);

            return Result.Ok();
        }

        _logger.LogWarning(
            "Actor not found for removal: SessionId={SessionId}",
            sessionId);

        return Result.Failure(
            ErrorCode.GameActorNotFound,
            $"Actor not found for removal: SessionId={sessionId}");
    }

    public Result<ISessionActor> UnregisterActor(long sessionId)
    {
        if (_actorsBySessionId.TryRemove(sessionId, out var actor))
        {
            RemoveFromIndexes(actor, sessionId);

            _logger.LogDebug(
                "Actor unregistered (no dispose): ActorId={ActorId}, UserId={UserId}, Remaining={Count}",
                sessionId, actor.UserId, _actorsBySessionId.Count);

            return Result<ISessionActor>.Ok(actor);
        }

        _logger.LogDebug(
            "Actor not found for unregister: SessionId={SessionId}",
            sessionId);

        return Result<ISessionActor>.Failure(
            ErrorCode.GameActorNotFound,
            $"Actor not found for unregister: SessionId={sessionId}");
    }

    private void RemoveFromIndexes(ISessionActor actor, long sessionId)
    {
        _sessionIdByUserId.TryRemove(
            new KeyValuePair<long, long>(actor.UserId, sessionId));
        _sessionIdByReconnectKey.TryRemove(
            new KeyValuePair<Guid, long>(actor.ReconnectKey, sessionId));
    }

    public Result RekeyActor(long oldSessionId, long newSessionId)
    {
        if (!_actorsBySessionId.TryRemove(oldSessionId, out var actor))
        {
            _logger.LogWarning(
                "RekeyActor: Actor not found for old SessionId={OldSessionId}",
                oldSessionId);
            return Result.Failure(
                ErrorCode.GameActorNotFound,
                $"Actor not found for SessionId: {oldSessionId}");
        }

        if (!_actorsBySessionId.TryAdd(newSessionId, actor))
        {
            // Rollback: 새 키가 이미 존재하면 원래 키로 복원
            _actorsBySessionId.TryAdd(oldSessionId, actor);
            _logger.LogWarning(
                "RekeyActor: New SessionId={NewSessionId} already exists, rolled back",
                newSessionId);
            return Result.Failure(
                ErrorCode.GameActorAddFailed,
                $"New SessionId already exists: {newSessionId}");
        }

        // Actor의 ActorId 갱신
        actor.ActorId = newSessionId;

        // UserId 역인덱스 갱신
        if (actor.UserId != 0)
            _sessionIdByUserId[actor.UserId] = newSessionId;

        // ReconnectKey 역인덱스 갱신
        _sessionIdByReconnectKey.TryRemove(
            new KeyValuePair<Guid, long>(actor.ReconnectKey, oldSessionId));
        _sessionIdByReconnectKey[actor.ReconnectKey] = newSessionId;

        _logger.LogInformation(
            "Actor rekeyed: OldSessionId={OldSessionId}, NewSessionId={NewSessionId}, UserId={UserId}",
            oldSessionId, newSessionId, actor.UserId);

        return Result.Ok();
    }

    public void UpdateReconnectKey(long sessionId, Guid oldKey, Guid newKey)
    {
        // 기존 키 제거
        _sessionIdByReconnectKey.TryRemove(new KeyValuePair<Guid, long>(oldKey, sessionId));

        // 새 키 추가
        _sessionIdByReconnectKey[newKey] = sessionId;

        _logger.LogDebug(
            "ReconnectKey updated: SessionId={SessionId}, OldKey={OldKey}, NewKey={NewKey}",
            sessionId, oldKey, newKey);
    }
}
