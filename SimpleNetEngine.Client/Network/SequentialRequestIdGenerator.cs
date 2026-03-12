using System.Threading;

namespace SimpleNetEngine.Client.Network;

/// <summary>
/// 순차적으로 증가하는 RequestId 생성기 (1-65535 순환)
/// CAS(Compare-And-Swap) 패턴으로 lock-free 구현
/// </summary>
public class SequentialRequestIdGenerator : IRequestIdGenerator
{
    private int _counter;

    /// <summary>
    /// 다음 RequestId 생성 (0을 건너뜀)
    /// </summary>
    public ushort Next()
    {
        while (true)
        {
            int current = _counter;
            int next = (current + 1) & 0xFFFF;  // 0-65535 범위로 제한

            if (next == 0)
                next = 1;  // 0을 건너뛰고 1로 설정

            // CAS: current가 여전히 _counter 값이면 next로 교체
            if (Interlocked.CompareExchange(ref _counter, next, current) == current)
                return (ushort)next;

            // CAS 실패 시 다시 시도 (다른 스레드가 먼저 변경함)
        }
    }
}
