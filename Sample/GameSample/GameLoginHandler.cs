using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Session;

namespace GameSample;

/// <summary>
/// 앱 레벨 로그인 핸들러 샘플 구현
/// ILoginHandler를 구현하여 비즈니스 로직을 주입
/// </summary>
public class GameLoginHandler(ILogger<GameLoginHandler> logger) : ILoginHandler
{
    public Task<LoginAuthResult> AuthenticateAsync(ReadOnlyMemory<byte> credential, ISessionActor actor)
    {
        // TODO: 앱 정의 proto로 역직렬화 후 JWT 검증, OAuth, DB 유저 조회 등
        // 샘플에서는 credential 바이트를 UTF-8 문자열로 읽어 UserId로 변환
        var externalId = System.Text.Encoding.UTF8.GetString(credential.Span);

        if (long.TryParse(externalId, out var userId))
        {
            return Task.FromResult(LoginAuthResult.Success(userId));
        }

        // 문자열인 경우 해시 기반 변환
        var hashedId = (long)System.IO.Hashing.XxHash64.HashToUInt64(credential.Span);

        return Task.FromResult(LoginAuthResult.Success(hashedId));
    }

    public Task OnLoginSuccessAsync(ISessionActor actor)
    {
        logger.LogInformation("Login success hook: UserId={UserId}", actor.UserId);
        return Task.CompletedTask;
    }

    public Task OnReconnectedAsync(ISessionActor actor)
    {
        logger.LogInformation("Reconnected hook: UserId={UserId}", actor.UserId);
        return Task.CompletedTask;
    }

    public Task<DisconnectAction> OnKickoutAsync(ISessionActor actor, KickoutReason reason)
    {
        logger.LogWarning("Kickout hook: UserId={UserId}, Reason={Reason}", actor.UserId, reason);
        return Task.FromResult(DisconnectAction.TerminateSession);
    }

    public Task OnDisconnectedAsync(ISessionActor actor)
    {
        logger.LogInformation("Disconnected hook: UserId={UserId}", actor.UserId);
        return Task.CompletedTask;
    }

    public Task OnLogoutAsync(ISessionActor actor)
    {
        logger.LogInformation("Logout hook: UserId={UserId}", actor.UserId);
        return Task.CompletedTask;
    }
}
