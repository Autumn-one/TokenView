using System.Runtime;
using TokenView.Wpf.Data.Collectors;
using TokenView.Wpf.Data.Usage;
using TokenView.Wpf.Data;

namespace TokenView.Wpf.Services;

/// <summary>后台同步：独立连接、串行执行，结果通过事件回到界面线程。</summary>
public sealed class SyncService
{
    public static SyncService Current { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pendingClear;

    public event Action<SyncProgress>? Progress;
    public event Action<SyncBatch?, string?>? Completed;

    public bool IsRunning { get; private set; }
    public long? StartedAtMs { get; private set; }
    public long? FinishedAtMs { get; private set; }
    public string? HardError { get; private set; }
    public SyncBatch? LastBatch { get; private set; }
    public Task? CurrentTask { get; private set; }
    public string? QoderRecordingError { get; private set; }
    public Dictionary<string, ClientSyncResult> LastResults { get; } = new(StringComparer.Ordinal);

    public async Task PrepareQoderRecordingAsync(IReadOnlyCollection<string> clients)
    {
        QoderRecordingError = await QoderUsageRecordingService.EnsureEnabledAsync(clients)
            .ConfigureAwait(false);
    }

    public bool TryStart(IReadOnlyCollection<string> clients, bool force)
    {
        if (Volatile.Read(ref _pendingClear) != 0) return false;
        if (!_gate.Wait(0)) return false;
        if (Volatile.Read(ref _pendingClear) != 0)
        {
            _gate.Release();
            return false;
        }
        IsRunning = true;
        StartedAtMs = UsageTime.NowMs();
        HardError = null;
        LastBatch = null;
        var selected = clients.Count == 0 ? Array.Empty<string>() : clients.ToArray();
        CurrentTask = Task.Run(async () =>
        {
            try
            {
                await PrepareQoderRecordingAsync(selected).ConfigureAwait(false);
                using var store = UsageStore.Open(TokenViewDatabase.DatabasePath);
                var batch = SyncEngine.Sync(store, new SyncOptions(force), selected,
                    progress => Progress?.Invoke(progress));
                LastBatch = batch;
                foreach (var client in batch.Clients)
                {
                    LastResults[client.Client] = client;
                }
                HardError = batch.HasBlockingErrors()
                    ? "部分数据源同步失败，其他数据源已继续处理；请查看各客户端状态"
                    : null;
            }
            catch (Exception error)
            {
                HardError = $"同步失败，请检查数据源与存储权限（{error.GetType().Name}）";
            }
            finally
            {
                FinishedAtMs = UsageTime.NowMs();
                IsRunning = false;
                Completed?.Invoke(LastBatch, HardError);
                // 整轮采集会制造大量短命大对象（JSONL 行 >85 KB 直接进 LOH）。
                // 只在这一处做一次压缩回收，避免 LOH 段把私有内存留在峰值。
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                _gate.Release();
            }
        });
        return true;
    }

    public async Task ClearStatisticsAsync(string databasePath, Action pauseAndSave, LanSyncService lan)
    {
        Interlocked.Increment(ref _pendingClear);
        try
        {
            // 先保存暂停与禁用局域网，即使此后退出也不会在重启时重新填充统计。
            pauseAndSave();
            await _gate.WaitAsync();
            try
            {
                await lan.StopAndDrainAsync();
                await Task.Run(() =>
                {
                    using var store = UsageStore.Open(databasePath);
                    store.ClearStatistics();
                });
                LastBatch = null;
                LastResults.Clear();
                HardError = null;
                lan.NotifyPeersChanged();
                Completed?.Invoke(null, null);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingClear);
        }
    }
}
