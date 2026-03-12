using System;

namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// 특정 패킷 핸들러가 요구하는 최소 Actor 상태(State)를 명시합니다.
/// Dispatcher 층위에서 이 속성을 파싱하여 조건 미달 시 패킷을 조기 차단합니다.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class RequireActorStateAttribute : Attribute
{
    public ActorState[] AllowedStates { get; }

    /// <param name="allowedStates">이 핸들러를 실행하기 위해 허용되는 Actor 상태 목록</param>
    public RequireActorStateAttribute(params ActorState[] allowedStates)
    {
        AllowedStates = allowedStates;
    }
}
