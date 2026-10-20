using System.Text;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data;
using TokenView.Wpf.Data.Collectors;
using TokenView.Wpf.Data.Usage;
using TokenView.Wpf.Services;

if (args.Length == 1 && args[0] is "announcement" or "about-info")
{
    return await FetchContentAsync(args[0]);
}

if (args.Length < 2)
{
    Console.WriteLine("usage: probe copy <source> <target> | probe sync <db> [--force] [clients...] | " +
                      "probe totals <db> | probe announcement | probe about-info");
    return 1;
}

var mode = args[0];
var database = args[1];

switch (mode)
{
    case "copy":
    {
        var source = database;
        var target = args[2];
        if (File.Exists(target)) File.Delete(target);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", target);
        command.ExecuteNonQuery();
        Console.WriteLine($"copied {source} -> {target}");
        return 0;
    }
    case "sync":
    {
        var force = args.Contains("--force");
        var clients = args.Skip(2).Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (clients.Count == 0) clients = SyncEngine.AllClients.ToList();
        using var store = UsageStore.Open(database);
        var batch = SyncEngine.Sync(store, new SyncOptions(force), clients,
            progress => Console.WriteLine($"progress {progress.CompletedClients}/{progress.TotalClients} {progress.CurrentClient}"));
        foreach (var client in batch.Clients)
        {
            var report = client.Report;
            Console.WriteLine(report is null
                ? $"{client.Client}: ERROR retryable={client.Retryable} {client.Error}"
                : $"{client.Client}: root_missing={report.RootMissing} seen={report.FilesSeen} parsed={report.FilesParsed} unchanged={report.FilesSkippedUnchanged} sessions={report.SessionsImported} inserted={report.MessagesInserted} pruned={report.MessagesPruned} unparsed={report.UnparsedLines} ignored={report.IgnoredLines} failed={report.FilesFailed} dirs_failed={report.DirectoriesFailed}");
        }
        Console.WriteLine($"blocking={batch.HasBlockingErrors()} retryable={batch.HasRetryableErrors()}");
        WriteMemory();
        return 0;
    }
    case "autostart":
    {
        var settings = new TokenView.Wpf.Settings.AppSettings
        {
            Autostart = args[1] == "on",
            AutostartHidden = true,
        };
        AutostartService.Apply(settings);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        Console.WriteLine($"TokenView={key?.GetValue("TokenView") ?? "<none>"}");
        return 0;
    }
    case "ledger":
    {
        try
        {
            var ledger = TokenViewDatabase.LoadLedger(TokenViewDatabase.CreateRange(database));
            Console.WriteLine($"sessions={ledger.Sessions} tokens={ledger.Tokens} today={ledger.TodayTokens} " +
                              $"messages={ledger.Messages} cacheBps={ledger.CacheHitBps} daily={ledger.Daily.Count}");
        }
        catch (Exception error)
        {
            Console.WriteLine(error);
        }
        return 0;
    }
    case "model":
        Console.WriteLine(TokenView.Wpf.ModelIconData.KindOf(database));
        return 0;
    case "update":
    {
        var info = await UpdateService.CheckAsync();
        Console.WriteLine(info is null
            ? $"up to date ({UpdateService.CurrentVersion})"
            : $"update {info.Version} {info.DownloadUrl} notes={info.Notes?.Length ?? 0}");
        return 0;
    }
    case "relocate":
    {
        if (args.Length < 5)
        {
            Console.WriteLine("usage: probe relocate <sourceDb> <sourceSettings> <target> <locator>");
            return 1;
        }
        var sourceDb = database;
        var sourceSettings = args[2];
        var target = args[3];
        var locator = args[4];
        var current = Path.Combine(Path.GetTempPath(), $"tokenview-relocate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(current);
        File.Copy(sourceDb, Path.Combine(current, "tokenview-usage.sqlite3"));
        if (File.Exists(sourceSettings))
        {
            File.Copy(sourceSettings, Path.Combine(current, "settings.json"));
        }
        var result = StorageRelocationService.Relocate(target, current, locator);
        Console.WriteLine($"moved to {result.Directory} warning={result.Warning ?? "none"}");
        Console.WriteLine($"target files: {string.Join(", ", Directory.EnumerateFiles(target).Select(Path.GetFileName))}");
        Console.WriteLine($"old files: {string.Join(", ", Directory.EnumerateFiles(current).Select(Path.GetFileName))}");
        Console.WriteLine($"locator: {File.ReadAllText(locator)}");
        return 0;
    }
    case "totals":
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT client, COUNT(*), COALESCE(SUM(requests),0), COALESCE(SUM(total_tokens),0)
            FROM request_hourly GROUP BY client ORDER BY total_tokens DESC;
            """;
        using var reader = command.ExecuteReader();
        long total = 0;
        while (reader.Read())
        {
            var tokens = reader.GetInt64(3);
            total += tokens;
            Console.WriteLine($"{reader.GetString(0)}\trows={reader.GetInt64(1)}\trequests={reader.GetInt64(2)}\ttokens={tokens}");
        }
        Console.WriteLine($"TOTAL\t{total:0}");
        using var sessions = connection.CreateCommand();
        sessions.CommandText = "SELECT COUNT(*) FROM sessions";
        Console.WriteLine($"sessions={sessions.ExecuteScalar()}");
        return 0;
    }
    default:
        Console.WriteLine($"unknown mode {mode}");
        return 1;
}

static void WriteMemory()
{
    using var process = System.Diagnostics.Process.GetCurrentProcess();
    var info = GC.GetGCMemoryInfo();
    Console.WriteLine(
        $"memory: peak_ws={process.PeakWorkingSet64 / 1048576}MB ws={process.WorkingSet64 / 1048576}MB " +
        $"private={process.PrivateMemorySize64 / 1048576}MB gc_heap={info.HeapSizeBytes / 1048576}MB " +
        $"committed={info.TotalCommittedBytes / 1048576}MB");
}

static async Task<int> FetchContentAsync(string mode)
{
    var kind = mode == "announcement" ? ContentKind.Announcement : ContentKind.PersonalInfo;
    var service = MirrorContentService.Shared;
    var config = await service.ResolveConfigAsync(MirrorsConfig.Default, CancellationToken.None);
    var result = await service.FetchAsync(config, kind, CancellationToken.None);
    if (!result.Success)
    {
        Console.WriteLine($"error: {result.ErrorMessage}");
        return 1;
    }

    var markdown = result.Markdown ?? "";
    var title = markdown.Split('\n')
        .FirstOrDefault(line => line.TrimStart().StartsWith('#'))?.Trim() ?? "";
    Console.WriteLine($"source={result.SourceId}");
    Console.WriteLine($"url={result.SourceUrl}");
    Console.WriteLine($"hash={result.ContentHash}");
    Console.WriteLine($"bytes={Encoding.UTF8.GetByteCount(markdown)}");
    Console.WriteLine($"chars={markdown.Length}");
    Console.WriteLine($"fetched={result.FetchedAtUtc:O}");
    Console.WriteLine($"title={title}");
    return 0;
}
