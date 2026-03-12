using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Node.Core;

public class QueuedResponseWriter<T> : IDisposable
{
    private readonly Channel<T> _channel;
    private readonly Task _consumerTask;
    private readonly Func<T, Task> _func;
    private readonly ILogger _logger;

    public QueuedResponseWriter(Func<T, Task> func, ILogger logger)
    {
        _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });

        _func = func;
        _logger = logger;

        _consumerTask = ConsumeQueueAsync();
    }

    public void Write(in T value)
    {
        _channel.Writer.TryWrite(value);
    }

    async Task ConsumeQueueAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        await _func(item);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error occurred while processing item in queue. The queue will continue processing next items.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in queue consumer loop. The queue has stopped.");
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        
        try
        {
            _consumerTask.Wait(TimeSpan.FromSeconds(5)); // 타임아웃 추가
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Consumer task did not complete gracefully");
        }
    }
}