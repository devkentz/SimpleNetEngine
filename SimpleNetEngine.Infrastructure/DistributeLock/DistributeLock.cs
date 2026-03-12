using StackExchange.Redis;

namespace SimpleNetEngine.Infrastructure.DistributeLock;

public static class RedisDatabaseExtension
{
    public static async Task<DistributeLockObject?> TryAcquireLockAsync(this IDatabase database, string key,
        int expirySeconds = 10)
        => await DistributeLockObject.CreateAsync(database, key, expirySeconds);
}

public class DistributeLockObject : IAsyncDisposable
{
    private readonly TimeSpan _lockExpiry;
    private readonly IDatabase _database;
    private readonly string _lockKey;
    private readonly string _lockValue = Guid.NewGuid().ToString();
    public bool IsLock { get; private set; }

    private DistributeLockObject(IDatabase database, string lockKey, int expirySec = 10)
    {
        _database = database;
        _lockKey = lockKey;
        _lockExpiry = TimeSpan.FromSeconds(expirySec);
    }

    public static async Task<DistributeLockObject?> CreateAsync(IDatabase database, string lockKey,
        int expirySeconds = 10)
    {
        var lockObj = new DistributeLockObject(database, lockKey, expirySeconds);
        await lockObj.LockAsync();
        
        if(lockObj.IsLock)
            return lockObj;

        await lockObj.DisposeAsync();
        return null;
    }

    private async Task LockAsync(int retryDelayMs = 100, int maxRetryCount = 50)
    {
        for (var i = 0; i < maxRetryCount; i++)
        {
            var isLockAcquired = await _database.LockTakeAsync(_lockKey, _lockValue, _lockExpiry);
            if (isLockAcquired)
            {
                IsLock = true;
                return;
            }

            await Task.Delay(retryDelayMs);
        }

        IsLock = false;
    }


    public async ValueTask DisposeAsync()
    {
        if (!IsLock)
            return;
        
        await _database.LockReleaseAsync(_lockKey, _lockValue);
        IsLock = false;
    }
}