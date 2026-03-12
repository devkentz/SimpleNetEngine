namespace SimpleNetEngine.Infrastructure;

/// <summary>
/// 애플리케이션 종료를 요청하는 역할을 추상화한 인터페이스입니다.
/// </summary>
public interface IApplicationStopper
{
    /// <summary>
    /// 애플리케이션의 우아한 종료(Graceful Shutdown)를 요청합니다.
    /// </summary>
    void StopApplication();
}
