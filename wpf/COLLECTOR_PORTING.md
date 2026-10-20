# Collector implementation contract (C# WPF)

This document is the contract for adding or changing a collector in
`wpf/TokenView.Wpf/Data/Collectors/`. The existing collectors there are the reference
implementation; a new collector must satisfy the same invariants.

## Output

One file: `wpf/TokenView.Wpf/Data/Collectors/<Name>Collector.cs`.

- Namespace `TokenView.Wpf.Data.Collectors`; `public sealed class <Name>Collector : IClientCollector`.
- `ClientKey` is the exact client string used by TokenView (`codex`, `claude_code`, …).
- A public parameterless constructor resolves default roots from environment variables
  (`CollectorSupport.HomeDir()` / `EnvPath(name)`); optional additional constructors
  accepting explicit paths are used by fixtures.
- `SourcePaths` returns every configured location, including missing ones, for the
  settings diagnostics list.
- `Sync(UsageStore store, SyncOptions options)` returns a `SyncReport`.
- Keep changes scoped to the collector file. If a shared helper is missing, report it
  instead of quietly changing shared code.

## Registration checklist

A new client key is only visible and syncable once it is registered everywhere:

| Place | File | Purpose |
| --- | --- | --- |
| `AppSettings.AllClients` | `Settings/AppSettings.cs` | canonical key list; `EnabledClients` is validated against it |
| `SyncEngine.AllClients` | `Data/Collectors/SyncEngine.cs` | `Sync` filters clients against this list (collector instances are discovered by reflection over `IClientCollector`) |
| `TokenViewDatabase.Clients` | `Data/TokenViewDatabase.cs` | display label and brand color |
| Settings source list | `SettingsWindow.xaml.cs` (`BuildClientList`) | per-client enable checkbox and `SourcePaths` diagnostics |
| Docs | `docs/data-model.md`, `docs/data-source-research.md` | source layout and statistics contract |

## Shared API

`Data/Usage/TokenSemantics.cs` — `TokenSemanticsCatalog.For(client)` plus per-client
constants (`Codex`, `ClaudeCode`, `QoderIde`, `QoderCli`, …); Qoder selects
`QoderIde` / `QoderCli` explicitly.

`Data/Usage/NormalizedUsage.cs`

- `RawTokenCounts(Input, Output, CacheRead, CacheWrite, Reasoning, ClientTotal)`; all
  fields are `long?`, where `null` means **missing** and must not be treated as zero.
- `UsageNormalizer.Normalize(semantics, raw)` returns buckets / total / unknown relations.
  Prefer `RequestBuilder`, which does this for you.

`Data/Collectors/RequestBuilder.cs`

- `Request(client, sessionId, RequestMeta, raw)` / `MessageWithSemantics(...)` → `MessageRecord`;
  `Session(client, SessionMeta, messages, counts, partial)` / `SessionWithSemantics(...)` → `SessionRecord`.
- `SumCompleteCounts(counts)` — a group with any missing field stays missing; overflow zeroes the group.
- `RequestMeta(Key, Model, Agent, CreatedMs, CompletedMs, Sidechain, SourceRef)`;
  `SessionMeta(Id, Parent, Project, StartMs, EndMs)`.
- When a client has a bespoke session rollup (Codex cumulative, OpenCode session rows, …),
  construct `SessionRecord` / `MessageRecord` directly instead of using `RequestBuilder`.

`Data/Collectors/CollectorSupport.cs`

- `HomeDir()`, `EnvPath(name)`, `Combine(root, parts...)`
- `JsonlFiles(root, report)`, `DiscoverFiles(root, report, predicate)`,
  `Fingerprint(path)` → `(long Size, long MtimeMs)`
- `ReadJsonLines(path, (line, complete) => …)`; `complete == false` means the last line
  has no newline. A broken final line is an I/O-level source error (retry later via
  `SyncException.FromSource`); a broken complete line increments `UnparsedLines`.
- `String/Long/Bool/Element` `JsonElement` helpers.
- `ValidCounts(raw)` rejects negative or overflowing counts; drop such records.
- `NewSourceFile(...)`, `IsUnchanged(...)` (size + mtime + parser version), `CountReason(...)`.

`Data/Collectors/SqliteSnapshot.cs`

- `SqliteSnapshot.Open(path)` returns a snapshot connection over a temp copy with
  committed WAL frames applied; dispose it when done. Never open the original client DB.
- `SqliteSnapshot.Signature(path)` gives `(main, wal?)` fingerprints. Use it for the
  unchanged check and the "changed during scan" guard.

`Data/Collectors/CollectorContracts.cs`

- `SyncReport` fields: `RootMissing`, `FilesSeen`, `FilesParsed`, `FilesSkippedUnchanged`,
  `FilesWithoutIdentity`, `FilesFailed`, `DirectoriesFailed`, `UnparsedLines`,
  `IgnoredLines`, `SessionsImported`, `SessionsSkippedStale`, `MessagesInserted`,
  `MessagesUpdated`, `MessagesSkippedStale`, `MessagesPruned`, plus
  `Dictionary<ScanReason, int> Reasons` (`New`, `Appended`, `TruncatedOrReplaced`,
  `ParserVersionChanged`, `Forced`, `Unchanged`).
- `SyncException` factories: `Directory`, `FromSource`, `Database`, `Store`,
  `ChangedDuringScan`. `ChangedDuringScan` is the only retryable error.

`Data/Usage/UsageStore.cs`

- `SourceFile(client, path)` returns `SourceFileRow?` (the watermark).
- `ImportSession(file, session, messages)` / `ReplaceFileImport(file, session, messages)`
  for per-file incremental import.
- `ReplaceClientSnapshot(client, files, sessions)` / `MergeClientSnapshot(...)` for
  snapshot-style clients (`SessionImport(session, messages, sourcePath, messageSourcePaths)`);
  `Replace` rewrites the client's whole snapshot atomically (rows for vanished files are
  pruned), `Merge` replaces only the sessions given.
- Writes are already transactional, idempotent (unique constraints below), and mark the
  request rollup dirty.

## Rules

1. Read-only toward client data; never write, move or delete client files.
2. Only metadata. Never persist or log message bodies.
3. Preserve dedup keys, source refs, session ids and token semantics literally.
4. Unknown fields are ignored; unknown record kinds are counted in `IgnoredLines`
   where the collector recognizes them as ignorable, not treated as parse failures.
5. Root missing is a normal `RootMissing` report, not an exception.
6. Honor the watermark: when `SyncOptions.ForceRescan` is false, skip files whose
   size/mtime/parser version are unchanged (`FilesSkippedUnchanged`); where nothing
   changed at all, skip the client without writing.
7. If the source changes during a scan (path list or fingerprint), throw
   `SyncException.ChangedDuringScan()` so the batch retries later.
8. Use ordinal string comparisons for ids, paths and ordering.
9. Keep the UI thread out of it; all collectors run on a background thread
   (`SyncService` → `SyncEngine.Sync`).
10. Row expectations: one `sessions` row per `(client, client_session_id)`, one `messages`
    row per `(client, session_id, dedup_key)`; re-scanning the same data must not duplicate rows.

## Verification

Unit tests for service-layer logic live in `wpf/TokenView.Tests` (xUnit). Collector changes are
verified through `wpf/TokenView.Probe` on a **copy** of the database (never sync into the live one),
and the service tests run with:

```bash
dotnet test wpf/TokenView.Tests/TokenView.Tests.csproj -c Release
```

```bash
dotnet build wpf/TokenView.Probe/TokenView.Probe.csproj -c Release
P=wpf/TokenView.Probe/bin/Release/net10.0-windows/TokenView.Probe.dll
dotnet $P copy "$APPDATA/com.tokenview.app/tokenview-usage.sqlite3" /tmp/db.sqlite3
dotnet $P sync /tmp/db.sqlite3 <client>
dotnet $P totals /tmp/db.sqlite3
```

Compare with the same client in the live DB via
`SELECT COUNT(*), SUM(total_tokens) FROM sessions WHERE client='<client>';`.
Also build a synthetic fixture (no real transcript content) covering: a normal record,
a duplicate id, a missing token field, an unknown event type, an unchanged second scan
(must skip), and a truncated/replaced file.
