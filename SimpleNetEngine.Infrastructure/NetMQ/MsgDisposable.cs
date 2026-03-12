using NetMQ;

namespace SimpleNetEngine.Infrastructure.NetMQ;

/// <summary>
/// NetMQ Msg의 IDisposable 래퍼
/// using 구문으로 안전한 리소스 관리 가능
/// </summary>
public class MsgDisposable : IDisposable
{
    private Msg _msg;

    public Msg Value => _msg;

    /// <summary>
    /// Msg의 ref 반환 (Send 시 사용)
    /// </summary>
    public ref Msg GetRef() => ref _msg;

    /// <summary>
    /// 빈 Msg 생성
    /// </summary>
    public MsgDisposable()
    {
        _msg = new Msg();
    }

    /// <summary>
    /// Pool에서 Msg 할당
    /// </summary>
    public MsgDisposable(int size)
    {
        _msg = new Msg();
        _msg.InitPool(size);
    }

    /// <summary>
    /// GC 메모리로 Msg 생성 (Zero-copy)
    /// </summary>
    public MsgDisposable(byte[] data)
    {
        _msg = new Msg();
        _msg.InitGC(data, data.Length);
    }

    /// <summary>
    /// 기존 Msg를 래핑
    /// </summary>
    public MsgDisposable(Msg msg)
    {
        _msg = msg;
    }

    public void Dispose()
    {
        if (_msg.IsInitialised)
        {
            _msg.Close();
        }
    }
}
