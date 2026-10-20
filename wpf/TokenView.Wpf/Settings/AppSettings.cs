using System.Text.Json.Serialization;

namespace TokenView.Wpf.Settings;

public enum CloseBehavior
{
    [JsonStringEnumMemberName("ask")]
    Ask,
    [JsonStringEnumMemberName("tray")]
    Tray,
    [JsonStringEnumMemberName("exit")]
    Exit,
}

public enum AppTheme
{
    [JsonStringEnumMemberName("system")]
    System,
    [JsonStringEnumMemberName("light")]
    Light,
    [JsonStringEnumMemberName("dark")]
    Dark,
}

public enum TokenDisplay
{
    [JsonStringEnumMemberName("auto")]
    Auto,
    [JsonStringEnumMemberName("chinese")]
    Chinese,
    [JsonStringEnumMemberName("k")]
    K,
    [JsonStringEnumMemberName("m")]
    M,
    [JsonStringEnumMemberName("b")]
    B,
    [JsonStringEnumMemberName("exact")]
    Exact,
}

public sealed class AppSettings
{
    public static readonly string[] AllClients =
    [
        "codex", "claude_code", "opencode", "dsh", "kimi_code", "mimocode",
        "pi_desktop", "pi_agent", "workbuddy", "copilot", "gemini_cli", "grok",
        "mcode", "devin", "command_code", "cline", "qoder", "codearts",
    ];

    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Ask;
    public bool Autostart { get; set; }
    public bool AutostartHidden { get; set; } = true;
    public bool RememberWindow { get; set; } = true;
    public bool SidebarCollapsed { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Light;
    public TokenDisplay TokenDisplay { get; set; } = TokenDisplay.Auto;
    public string DefaultRange { get; set; } = "7d";
    public bool SyncOnStart { get; set; } = true;
    public int SyncIntervalMinutes { get; set; } = 5;
    public bool CollectionPaused { get; set; }
    public List<string> EnabledClients { get; set; } = [.. AllClients];
    public bool AutomaticUpdates { get; set; }

    public AppSettings Clone() => new()
    {
        CloseBehavior = CloseBehavior,
        Autostart = Autostart,
        AutostartHidden = AutostartHidden,
        RememberWindow = RememberWindow,
        SidebarCollapsed = SidebarCollapsed,
        Theme = Theme,
        TokenDisplay = TokenDisplay,
        DefaultRange = DefaultRange,
        SyncOnStart = SyncOnStart,
        SyncIntervalMinutes = SyncIntervalMinutes,
        CollectionPaused = CollectionPaused,
        EnabledClients = [.. EnabledClients],
        AutomaticUpdates = AutomaticUpdates,
    };
}
