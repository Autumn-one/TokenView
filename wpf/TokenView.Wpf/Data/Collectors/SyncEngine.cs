using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

/// <summary>独立扫描各客户端；单个失败不会中断其余采集器。</summary>
public static class SyncEngine
{
    public static readonly string[] AllClients =
    [
        "codex", "claude_code", "opencode", "dsh", "kimi_code", "mimocode", "pi_desktop",
        "pi_agent", "workbuddy", "copilot", "gemini_cli", "grok", "mcode", "devin",
        "command_code", "cline", "qoder", "codearts",
    ];

    private static Dictionary<string, IClientCollector>? _collectors;

    public static IReadOnlyDictionary<string, IClientCollector> Collectors()
    {
        if (_collectors is not null) return _collectors;
        var discovered = typeof(IClientCollector).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IClientCollector).IsAssignableFrom(type))
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => (IClientCollector)Activator.CreateInstance(type)!)
            .GroupBy(collector => collector.ClientKey)
            .ToDictionary(group => group.Key, group => group.First());
        _collectors = discovered;
        return discovered;
    }

    public static IClientCollector? Find(string client) => Collectors().GetValueOrDefault(client);

    public static SyncBatch Sync(UsageStore store, SyncOptions options, IEnumerable<string> clients,
        Action<SyncProgress>? onProgress = null)
    {
        var selected = clients.Distinct(StringComparer.Ordinal)
            .Where(key => AllClients.Contains(key, StringComparer.Ordinal))
            .ToList();
        var batch = new SyncBatch();
        onProgress?.Invoke(new SyncProgress(0, selected.Count, selected.FirstOrDefault()));
        foreach (var client in selected)
        {
            var collector = Find(client);
            ClientSyncResult result;
            if (collector is null)
            {
                result = new ClientSyncResult(client, new SyncReport { RootMissing = true }, null, false);
            }
            else
            {
                try
                {
                    result = new ClientSyncResult(client, collector.Sync(store, options), null, false);
                }
                catch (SyncException error)
                {
                    result = new ClientSyncResult(client, null, error.Message, error.Retryable);
                }
                catch (Exception error)
                {
                    result = new ClientSyncResult(client, null, error.Message, false);
                }
            }
            batch.Clients.Add(result);
            onProgress?.Invoke(new SyncProgress(batch.Clients.Count, selected.Count,
                selected.Count > batch.Clients.Count ? selected[batch.Clients.Count] : null));
        }
        if (!store.RequestRollupIsFresh())
        {
            store.RebuildRequestRollup();
        }
        store.Checkpoint();
        return batch;
    }
}
