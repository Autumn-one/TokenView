using System.Text.Json;

namespace TokenView.Wpf.Services;

public sealed class MirrorsConfig
{
    private const string RawRoot = "https://raw.githubusercontent.com";

    private static readonly (int Priority, string Id, string Prefix)[] ContentProxies =
    [
        (10, "gitwarp", "https://proxy.gitwarp.top/"),
        (20, "ghfast", "https://ghfast.top/"),
        (30, "gh-proxy", "https://gh-proxy.org/"),
        (40, "v4-gh-proxy", "https://v4.gh-proxy.org/"),
        (50, "v6-gh-proxy", "https://v6.gh-proxy.org/"),
        (60, "cdn-gh-proxy", "https://cdn.gh-proxy.org/"),
        (100, "github", ""),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static MirrorsConfig Default { get; } = CreateDefault();

    public RepoConfig Github { get; init; } = new("Autumn-one", "key-doing", "", "");

    public RepoConfig Gitee { get; init; } = new("Autumn-one", "key-doing-info", "", "");

    public RepoConfig Announcement { get; init; } = new("Autumn-one", "notify", "main", "README.md");

    public RepoConfig PersonalInfo { get; init; } = new("Autumn-one", "notify", "main", "info.md");

    public IReadOnlyList<SourceConfig> ConfigSources { get; init; } = [];

    public IReadOnlyList<SourceConfig> AnnouncementSources { get; init; } = [];

    public IReadOnlyList<SourceConfig> PersonalInfoSources { get; init; } = [];

    public IReadOnlyList<SourceConfig> Sources(ContentKind kind) => kind switch
    {
        ContentKind.Announcement => AnnouncementSources,
        _ => PersonalInfoSources,
    };

    public string? ConfigUrl(SourceConfig source) => MirrorUrl.Expand(source.Url,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owner"] = Github.Owner,
            ["repo"] = Github.Repo,
            ["giteeOwner"] = Gitee.Owner,
            ["giteeRepo"] = Gitee.Repo,
        });

    public string? ContentUrl(SourceConfig source, ContentKind kind)
    {
        var repo = kind == ContentKind.Announcement ? Announcement : PersonalInfo;
        if (!repo.HasRepository || string.IsNullOrWhiteSpace(repo.Branch) || string.IsNullOrWhiteSpace(repo.File))
        {
            return null;
        }

        var prefix = kind == ContentKind.Announcement ? "announcement" : "personalInfo";
        return MirrorUrl.Expand(source.Url,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"{prefix}Owner"] = repo.Owner,
                [$"{prefix}Repo"] = repo.Repo,
                [$"{prefix}Branch"] = repo.Branch,
                [$"{prefix}File"] = repo.File,
            });
    }

    public static MirrorsConfig Merge(MirrorsConfig local, string json)
    {
        var remote = JsonSerializer.Deserialize<RemoteDocument>(json, JsonOptions)
                     ?? throw new JsonException("远程配置为空。");

        return new MirrorsConfig
        {
            Github = MergeRepo(local.Github, remote.Github),
            Gitee = MergeRepo(local.Gitee, remote.Gitee),
            Announcement = MergeRepo(local.Announcement, remote.Announcement),
            PersonalInfo = MergeRepo(local.PersonalInfo, remote.PersonalInfo),
            ConfigSources = MergeSources(local.ConfigSources, remote.ConfigSources),
            AnnouncementSources = MergeSources(local.AnnouncementSources, remote.AnnouncementSources),
            PersonalInfoSources = MergeSources(local.PersonalInfoSources, remote.PersonalInfoSources),
        };
    }

    private static RepoConfig MergeRepo(RepoConfig local, RemoteRepo? remote)
    {
        if (remote is null)
        {
            return local;
        }
        return local with
        {
            Owner = NonEmpty(remote.Owner) ?? local.Owner,
            Repo = NonEmpty(remote.Repo) ?? local.Repo,
            Branch = NonEmpty(remote.Branch) ?? local.Branch,
            File = NonEmpty(remote.File) ?? local.File,
        };
    }

    private static IReadOnlyList<SourceConfig> MergeSources(
        IReadOnlyList<SourceConfig> local,
        List<RemoteSource>? remote)
    {
        if (remote is null || remote.Count == 0)
        {
            return local;
        }
        return remote.Select((source, index) => new SourceConfig(
                NonEmpty(source.Id) ?? $"source-{index}",
                source.Enabled ?? true,
                source.Priority ?? 0,
                NonEmpty(source.Url) ?? ""))
            .ToList();
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static MirrorsConfig CreateDefault()
    {
        return new MirrorsConfig
        {
            ConfigSources =
            [
                new SourceConfig("config-gitee", true, 1,
                    "https://gitee.com/{giteeOwner}/{giteeRepo}/raw/master/mirrors.json"),
                new SourceConfig("config-gitwarp", true, 10,
                    "https://proxy.gitwarp.top/" + RawRoot + "/{owner}/{repo}/main/mirrors.json"),
                new SourceConfig("config-ghfast", true, 20,
                    "https://ghfast.top/" + RawRoot + "/{owner}/{repo}/main/mirrors.json"),
                new SourceConfig("config-github-release", true, 30,
                    "https://github.com/{owner}/{repo}/releases/latest/download/mirrors.json"),
                new SourceConfig("config-github-raw", true, 100,
                    RawRoot + "/{owner}/{repo}/main/mirrors.json"),
            ],
            AnnouncementSources = BuildContentSources("announce", "announcement"),
            PersonalInfoSources = BuildContentSources("personal-info", "personalInfo"),
        };
    }

    private static IReadOnlyList<SourceConfig> BuildContentSources(string idPrefix, string placeholderPrefix)
    {
        return ContentProxies.Select(proxy => new SourceConfig(
            $"{idPrefix}-raw-{proxy.Id}",
            true,
            proxy.Priority,
            proxy.Prefix + RawRoot +
            $"/{{{placeholderPrefix}Owner}}/{{{placeholderPrefix}Repo}}" +
            $"/{{{placeholderPrefix}Branch}}/{{{placeholderPrefix}File}}")).ToList();
    }

    private sealed class RemoteDocument
    {
        public RemoteRepo? Github { get; set; }

        public RemoteRepo? Gitee { get; set; }

        public RemoteRepo? Announcement { get; set; }

        public RemoteRepo? PersonalInfo { get; set; }

        public List<RemoteSource>? ConfigSources { get; set; }

        public List<RemoteSource>? AnnouncementSources { get; set; }

        public List<RemoteSource>? PersonalInfoSources { get; set; }
    }

    private sealed class RemoteRepo
    {
        public string? Owner { get; set; }

        public string? Repo { get; set; }

        public string? Branch { get; set; }

        public string? File { get; set; }
    }

    private sealed class RemoteSource
    {
        public string? Id { get; set; }

        public bool? Enabled { get; set; }

        public int? Priority { get; set; }

        public string? Url { get; set; }
    }
}
