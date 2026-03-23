using System.Text.Json;
using DFrame.Controller;

namespace StressTest.Controller;

/// <summary>
/// 테스트 실행 완료 시 results/ 폴더에 JSON 파일 자동 저장 + 콘솔 리포트 출력.
/// DFrame Web UI의 History 탭에서도 과거 결과 조회 가능.
/// </summary>
public sealed class JsonFileResultHistoryProvider : IExecutionResultHistoryProvider
{
    private readonly string _rootDir;
    private readonly List<ExecutionSummary> _list = [];
    private readonly Dictionary<ExecutionId, (ExecutionSummary Summary, SummarizedExecutionResult[] Results)> _results = new();

    public event Action? NotifyCountChanged;

    public JsonFileResultHistoryProvider(string rootDir = "results")
    {
        _rootDir = rootDir;
        Directory.CreateDirectory(_rootDir);
        LoadExisting();
    }

    public int GetCount() => _list.Count;

    public IReadOnlyList<ExecutionSummary> GetList() => _list;

    public (ExecutionSummary Summary, SummarizedExecutionResult[] Results)? GetResult(ExecutionId executionId)
    {
        if (_results.TryGetValue(executionId, out var result))
            return result;

        var file = FindFile(executionId);
        if (file == null) return null;

        var loaded = LoadFromFile(file);
        if (loaded == null) return null;

        _results[executionId] = loaded.Value;
        return loaded;
    }

    public void AddNewResult(ExecutionSummary summary, SummarizedExecutionResult[] results)
    {
        _list.Insert(0, summary);
        _results[summary.ExecutionId] = (summary, results);

        var timestamp = summary.StartTime.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{timestamp}_{summary.Workload}_{summary.ExecutionId}.json";
        var filePath = Path.Combine(_rootDir, fileName);

        var json = JsonSerializer.Serialize(
            new ResultFile(summary, results),
            ResultFile.JsonOptions);

        File.WriteAllText(filePath, json);
        Console.WriteLine($"  Result saved: {filePath}");

        PrintConsoleReport(summary, results);
        NotifyCountChanged?.Invoke();
    }

    private void LoadExisting()
    {
        if (!Directory.Exists(_rootDir)) return;

        foreach (var file in Directory.GetFiles(_rootDir, "*.json").OrderByDescending(f => f))
        {
            var loaded = LoadFromFile(file);
            if (loaded != null)
                _list.Add(loaded.Value.Summary);
        }
    }

    private static (ExecutionSummary Summary, SummarizedExecutionResult[] Results)? LoadFromFile(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<ResultFile>(json, ResultFile.JsonOptions);
            if (data == null) return null;
            return (data.Summary, data.Results);
        }
        catch
        {
            return null;
        }
    }

    private string? FindFile(ExecutionId executionId)
    {
        if (!Directory.Exists(_rootDir)) return null;
        return Directory.GetFiles(_rootDir, $"*{executionId}*.json").FirstOrDefault();
    }

    private static void PrintConsoleReport(ExecutionSummary summary, SummarizedExecutionResult[] workers)
    {
        long totalSucceed = 0, totalError = 0, totalComplete = 0;
        var minLatency = TimeSpan.MaxValue;
        var maxLatency = TimeSpan.MinValue;
        TimeSpan? medianSum = null, p90Sum = null, p95Sum = null;
        long totalElapsedTicks = 0;
        int validWorkers = 0;
        double totalRps = 0;

        foreach (var w in workers)
        {
            totalSucceed += w.SucceedCount;
            totalError += w.ErrorCount;
            totalComplete += w.CompleteCount;

            if (w.SucceedCount <= 0) continue;

            validWorkers++;
            if (w.Min < minLatency) minLatency = w.Min;
            if (w.Max > maxLatency) maxLatency = w.Max;
            totalElapsedTicks += w.TotalElapsed.Ticks;
            totalRps += w.Rps;

            if (w.Median.HasValue) medianSum = (medianSum ?? TimeSpan.Zero) + w.Median.Value;
            if (w.Percentile90.HasValue) p90Sum = (p90Sum ?? TimeSpan.Zero) + w.Percentile90.Value;
            if (w.Percentile95.HasValue) p95Sum = (p95Sum ?? TimeSpan.Zero) + w.Percentile95.Value;
        }

        var avgLatency = totalSucceed > 0
            ? TimeSpan.FromTicks(totalElapsedTicks / totalSucceed)
            : TimeSpan.Zero;

        if (minLatency == TimeSpan.MaxValue) minLatency = TimeSpan.Zero;
        if (maxLatency == TimeSpan.MinValue) maxLatency = TimeSpan.Zero;

        var rps = summary.RpsSum ?? totalRps;
        var errorRate = totalComplete > 0 ? (double)totalError / totalComplete * 100 : 0;

        Console.WriteLine();
        PrintLine();
        Print("■ Load Test Report");
        PrintLine();
        Console.WriteLine($"  Workload    : {summary.Workload}");
        Console.WriteLine($"  Workers     : {summary.WorkerCount}");
        Console.WriteLine($"  Concurrency : {summary.Concurrency}");
        Console.WriteLine($"  Duration    : {FormatDuration(summary.RunningTime ?? TimeSpan.Zero)}");
        Console.WriteLine();

        Print("▸ Requests");
        Console.WriteLine($"  Total       : {totalComplete,12:N0}");
        Console.WriteLine($"  Succeed     : {totalSucceed,12:N0}");
        Console.WriteLine($"  Failed      : {totalError,12:N0}");
        Console.WriteLine($"  RPS         : {rps,12:N1}");
        Console.WriteLine($"  Error Rate  : {errorRate,11:F2}%");
        Console.WriteLine();

        Print("▸ Latency");
        Console.WriteLine($"  {"Min",-10}: {FormatMs(minLatency),14}");
        Console.WriteLine($"  {"Avg",-10}: {FormatMs(avgLatency),14}");
        Console.WriteLine($"  {"Median",-10}: {FormatMs(validWorkers > 0 && medianSum.HasValue ? TimeSpan.FromTicks(medianSum.Value.Ticks / validWorkers) : null),14}");
        Console.WriteLine($"  {"P90",-10}: {FormatMs(validWorkers > 0 && p90Sum.HasValue ? TimeSpan.FromTicks(p90Sum.Value.Ticks / validWorkers) : null),14}");
        Console.WriteLine($"  {"P95",-10}: {FormatMs(validWorkers > 0 && p95Sum.HasValue ? TimeSpan.FromTicks(p95Sum.Value.Ticks / validWorkers) : null),14}");
        Console.WriteLine($"  {"Max",-10}: {FormatMs(maxLatency),14}");
        Console.WriteLine();

        if (workers.Length > 1)
        {
            Print("▸ Per-Worker Details");
            Console.WriteLine($"  {"Worker",-10} {"Succeed",10} {"Error",8} {"RPS",10} {"Avg",10} {"P95",10}");
            Console.WriteLine($"  {new string('─', 58)}");
            foreach (var w in workers)
            {
                Console.WriteLine(
                    $"  {w.WorkerId,-10} {w.SucceedCount,10:N0} {w.ErrorCount,8:N0} {w.Rps,10:N1} {FormatMs(w.Avg),10} {FormatMs(w.Percentile95),10}");
            }
            Console.WriteLine();
        }

        var errorWorkers = workers.Where(w => w.Error && w.ErrorMessage != null).ToArray();
        if (errorWorkers.Length > 0)
        {
            Print("▸ Errors");
            foreach (var w in errorWorkers)
                Console.WriteLine($"  [{w.WorkerId}] {w.ErrorMessage}");
            Console.WriteLine();
        }

        PrintLine();
        Console.WriteLine();
    }

    private static string FormatMs(TimeSpan? ts) => ts.HasValue ? $"{ts.Value.TotalMilliseconds:F2} ms" : "N/A";
    private static string FormatMs(TimeSpan ts) => $"{ts.TotalMilliseconds:F2} ms";

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s" : $"{ts.TotalSeconds:F1}s";

    private static void PrintLine() => Console.WriteLine($"  {"",60}".Replace(' ', '─'));
    private static void Print(string text) => Console.WriteLine($"  {text}");
}

public record ResultFile(ExecutionSummary Summary, SummarizedExecutionResult[] Results)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true
    };
}
