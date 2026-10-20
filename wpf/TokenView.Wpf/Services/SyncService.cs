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

    public event Action<SyncProgress>? Progress;
    public event Action<SyncBatch?, string?>? Completed;

    public bool IsRunning { get; private set; }
    public long? StartedAtMs { get; private set; }
    public long? FinishedAtMs { get; private set; }
    public string? HardError { get; private set; }
    public SyncBatch? LastBatch { get; private set; }
    public Task? CurrentTask { get; private set; }
    public Dictionary<string, ClientSyncResult> LastResults { get; } = new(StringComparer.Ordinal);

    public bool TryStart(IReadOnlyCollection<string> clients, bool force)
    {
        if (!_gate.Wait(0)) return false;
        IsRunning = true;
        StartedAtMs = UsageTime.NowMs();
        HardError = null;
        LastBatch = null;
        var selected = clients.Count == 0 ? Array.Empty<string>() : clients.ToArray();
        CurrentTask = Task.Run(() =>
        {
            try
            {
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
                HardError = error.Message;
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
}
