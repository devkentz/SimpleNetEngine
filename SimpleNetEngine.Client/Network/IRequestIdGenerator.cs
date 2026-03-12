namespace SimpleNetEngine.Client.Network;

/// <summary>
/// RequestId 생성기 인터페이스
/// </summary>
public interface IRequestIdGenerator
{
    /// <summary>
    /// 다음 RequestId 생성 (0이 아닌 값)
    /// </summary>
    ushort Next();
}
