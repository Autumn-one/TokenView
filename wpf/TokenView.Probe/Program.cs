using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using TokenView.Wpf.Data;
using TokenView.Wpf.Data.Collectors;
using TokenView.Wpf.Data.Peers;
using TokenView.Wpf.Data.Usage;
using TokenView.Wpf.Services;

if (args.Length == 1 && args[0] is "announcement" or "about-info")
{
    return await FetchContentAsync(args[0]);
}

if (args.Length < 2)
{
    Console.WriteLine("usage: probe copy <source> <target> | probe sync <db> [--force] [clients...] | " +
                      "probe totals <db> | probe peer-export <db> <out> [--device <id>] [--name <name>] | " +
                      "probe peer-import <db> <snapshot> | probe peer-totals <db> [local] [range] | " +
                      "probe peer-roundtrip <dbA> <dbB> <folder> | " +
                      "probe lan-peer <db> <name> [targetUdpPort] | probe announcement | probe about-info");
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
    case "peer-export":
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: probe peer-export <db> <out> [--device <id>] [--name <name>]");
            return 1;
        }
        var deviceId = Option(args, "--device") ?? Guid.NewGuid().ToString("N");
        var deviceName = Option(args, "--name") ?? Environment.MachineName;
        var document = PeerSnapshotBuilder.Build(database, deviceId, deviceName,
            UpdateService.CurrentVersion);
        PeerSnapshotSerializer.WriteFile(args[2], document);
        Console.WriteLine($"device={document.DeviceId} name={document.DeviceName} " +
                          $"daily={document.RequestDaily.Count} hourly={document.RequestHourly.Count} " +
                          $"sessions={document.SessionDaily.Count} " +
                          $"tokens={document.RequestDaily.Sum(row => row.Tokens)}");
        return 0;
    }
    case "peer-import":
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: probe peer-import <db> <snapshot>");
            return 1;
        }
        var document = PeerSnapshotSerializer.ReadFile(args[2]);
        var result = PeerSnapshotStore.Import(database, document);
        Console.WriteLine($"imported={result.Imported} stale={result.StaleSkipped} " +
                          $"device={result.DeviceId} name={result.DeviceName} " +
                          $"daily={result.RequestDailyRows} hourly={result.RequestHourlyRows}");
        return 0;
    }
    case "peer-totals":
    {
        var scope = args.Length > 2 &&
                    string.Equals(args[2], "local", StringComparison.OrdinalIgnoreCase)
            ? DeviceSelection.Local
            : DeviceSelection.All;
        var range = TokenViewDatabase.CreateRange(args.Length > 3 ? args[3] : "7d");
        var snapshot = TokenViewDatabase.Load(database, range, scope);
        Console.WriteLine($"scope={snapshot.Scope} tokens={snapshot.TotalTokens} " +
                          $"requests={snapshot.TotalRequests} sessions={snapshot.Sessions} " +
                          $"estimated={snapshot.EstimatedTokens} clients={snapshot.Clients.Count}");
        foreach (var row in snapshot.Clients)
        {
            Console.WriteLine(
                $"{row.Client.Key}\t{row.Client.Label}\t{row.Tokens}\t{row.Requests}\t{row.Sessions}");
        }
        return 0;
    }
    case "peer-roundtrip":
    {
        if (args.Length < 4)
        {
            Console.WriteLine("usage: probe peer-roundtrip <dbA> <dbB> <folder>");
            return 1;
        }
        var dbA = args[1];
        var dbB = args[2];
        var folder = args[3];
        System.IO.Directory.CreateDirectory(folder);
        var idA = Guid.NewGuid().ToString("N");
        var idB = Guid.NewGuid().ToString("N");
        var documentA = PeerSnapshotBuilder.Build(dbA, idA, "PC-A", UpdateService.CurrentVersion);
        var documentB = PeerSnapshotBuilder.Build(dbB, idB, "PC-B", UpdateService.CurrentVersion);
        PeerSnapshotSerializer.WriteFile(
            System.IO.Path.Combine(folder, "A.tokenview.json.gz"), documentA);
        PeerSnapshotSerializer.WriteFile(
            System.IO.Path.Combine(folder, "B.tokenview.json.gz"), documentB);
        var importedB = PeerSnapshotStore.Import(dbA, documentB);
        var importedA = PeerSnapshotStore.Import(dbB, documentA);
        var range = TokenViewDatabase.CreateRange("all");
        var localA = TokenViewDatabase.Load(dbA, range, DeviceSelection.Local);
        var localB = TokenViewDatabase.Load(dbB, range, DeviceSelection.Local);
        var allA = TokenViewDatabase.Load(dbA, range, DeviceSelection.All);
        var expected = localA.TotalTokens + localB.TotalTokens;
        Console.WriteLine($"importA={importedA.Imported} importB={importedB.Imported} " +
                          $"localA={localA.TotalTokens} localB={localB.TotalTokens} " +
                          $"combined={allA.TotalTokens} expected={expected}");
        var ok = allA.TotalTokens == expected &&
                 allA.TotalRequests == localA.TotalRequests + localB.TotalRequests;
        Console.WriteLine(ok ? "OK" : "MISMATCH");
        return ok ? 0 : 1;
    }
    case "lan-peer":
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: probe lan-peer <db> <name> [targetUdpPort=17831]");
            return 1;
        }
        var targetPort = args.Length > 3 && int.TryParse(args[3], out var parsedPort)
            ? parsedPort
            : 17831;
        var deviceId = Guid.NewGuid().ToString("N");
        var service = new LanSyncService();
        service.Start(new LanSyncOptions(database, deviceId, args[2],
            UpdateService.CurrentVersion, true, 0, 0, IPAddress.Loopback,
            [new IPEndPoint(IPAddress.Loopback, targetPort)], null,
            TimeSpan.FromSeconds(1)));
        Console.WriteLine($"lan-peer device={deviceId} name={args[2]} " +
                          $"udp={service.UdpPort} tcp={service.TcpPort} target={targetPort}");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
    default:
        Console.WriteLine($"unknown mode {mode}");
        return 1;
}

static string? Option(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
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
