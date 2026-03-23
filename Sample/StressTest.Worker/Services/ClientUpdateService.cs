using System.Collections.Concurrent;
using SimpleNetEngine.Client;

namespace StressTest.Worker.Services;

/// <summary>
/// 모든 NetClient의 Update()를 전용 쓰레드에서 일괄 처리.
/// 클라이언트 수가 증가하면 자동으로 쓰레드를 추가하여 부하 분산.
/// Workload마다 개별 Task.Delay 루프를 돌리는 것보다 ThreadPool 경합이 크게 감소.
///
/// 생명주기: Setup → Register() → Execute → TeardownAsync → Complete()
/// Reset()은 DFrame 워크로드 실행 단위(batch) 종료 후 한 번만 호출.
/// </summary>
public sealed class ClientUpdateService
{
    private const int ClientsPerThread = 50;

    private readonly ConcurrentDictionary<int, UpdateThread> _threads = new();
    private int _clientCount;

    public void Register(NetClient client)
    {
        var index = Interlocked.Increment(ref _clientCount) - 1;
        var threadIndex = index / ClientsPerThread;
        var thread = _threads.GetOrAdd(threadIndex, _ => new UpdateThread(threadIndex));
        thread.Add(client);
    }

    /// <summary>
    /// 모든 Update 쓰레드를 종료하고 상태를 초기화.
    /// 다음 워크로드 실행 시 새로운 쓰레드가 생성됨.
    /// </summary>
    public void Reset()
    {
        if (_clientCount > 0)
        {
            foreach (var thread in _threads.Values)
                thread.Stop();

            _threads.Clear();
            Interlocked.Exchange(ref _clientCount, 0);
        }
    }

    private sealed class UpdateThread
    {
        private readonly List<NetClient> _clients = [];
        private NetClient[]? _snapshot;
        private readonly Thread _thread;
        private volatile bool _running = true;

        public UpdateThread(int index)
        {
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = $"ClientUpdate-{index}"
            };
            _thread.Start();
        }

        public void Add(NetClient client)
        {
            lock (_clients)
            {
                _clients.Add(client);
                _snapshot = null;
            }
        }

        private void Loop()
        {
            while (_running)
            {
                var clients = _snapshot ??= [.. _clients];

                if (clients.Length == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                for (var i = 0; i < clients.Length; i++)
                {
                    var client = clients[i];
                    if (!client.IsDispose)
                    {
                        try
                        {
                            client.Update();
                        }
                        catch
                        {
                            // 미등록 MsgId 등 — 개별 클라이언트 예외가 쓰레드를 죽이지 않도록
                        }
                    }
                }
            }
        }

        public void Stop()
        {
            _running = false;
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
