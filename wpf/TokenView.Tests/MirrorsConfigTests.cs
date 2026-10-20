using TokenView.Wpf.Services;
using Xunit;

namespace TokenView.Tests;

public sealed class MirrorsConfigTests
{
    [Fact]
    public void DefaultConfigUsesRegisteredProxyOrder()
    {
        var config = MirrorsConfig.Default;
        Assert.Equal(new[] { 10, 20, 30, 40, 50, 60, 100 },
            config.AnnouncementSources.Select(source => source.Priority));
        Assert.StartsWith("https://proxy.gitwarp.top/https://raw.githubusercontent.com/",
            config.AnnouncementSources[0].Url);
        Assert.StartsWith("https://raw.githubusercontent.com/", config.AnnouncementSources[^1].Url);
        Assert.Equal("announce-raw-gitwarp", config.AnnouncementSources[0].Id);
        Assert.Equal("personal-info-raw-gitwarp", config.PersonalInfoSources[0].Id);
        Assert.Equal(5, config.ConfigSources.Count);
        Assert.Equal(1, config.ConfigSources[0].Priority);
        Assert.Equal(100, config.ConfigSources[^1].Priority);
    }

    [Fact]
    public void MergeOverridesOnlyNonEmptyRepositoryFields()
    {
        var merged = MirrorsConfig.Merge(MirrorsConfig.Default, """
            {
              "announcement": { "branch": "", "file": "next.md" },
              "github": { "repo": "renamed" },
              "unknownSection": { "owner": "ignored" }
            }
            """);

        Assert.Equal("main", merged.Announcement.Branch);
        Assert.Equal("next.md", merged.Announcement.File);
        Assert.Equal("Autumn-one", merged.Announcement.Owner);
        Assert.Equal("renamed", merged.Github.Repo);
        Assert.Equal("key-doing-info", merged.Gitee.Repo);
    }

    [Fact]
    public void MergeReplacesOnlyNonEmptySourceLists()
    {
        var unchanged = MirrorsConfig.Merge(MirrorsConfig.Default, """
            { "announcementSources": [], "configSources": [], "personalInfoSources": [] }
            """);
        Assert.Equal(MirrorsConfig.Default.AnnouncementSources.Count, unchanged.AnnouncementSources.Count);
        Assert.Equal(MirrorsConfig.Default.ConfigSources.Count, unchanged.ConfigSources.Count);
        Assert.Equal(MirrorsConfig.Default.PersonalInfoSources.Count, unchanged.PersonalInfoSources.Count);

        var replaced = MirrorsConfig.Merge(MirrorsConfig.Default, """
            {
              "announcementSources": [
                { "id": "remote", "enabled": false, "priority": 5, "url": "https://example.test/a.md" }
              ]
            }
            """);
        var source = Assert.Single(replaced.AnnouncementSources);
        Assert.Equal("remote", source.Id);
        Assert.False(source.Enabled);
        Assert.Equal(5, source.Priority);
        Assert.Equal(MirrorsConfig.Default.PersonalInfoSources.Count, replaced.PersonalInfoSources.Count);
    }

    [Fact]
    public void MergeIgnoresUnknownFields()
    {
        var merged = MirrorsConfig.Merge(MirrorsConfig.Default, """
            {
              "announcement": { "owner": "someone", "extra": "ignored" },
              "announcementSources": [
                { "id": "remote", "priority": 5, "url": "https://example.test/a.md", "extra": 1 }
              ],
              "futureField": [1, 2, 3]
            }
            """);

        Assert.Equal("someone", merged.Announcement.Owner);
        Assert.Equal("remote", merged.AnnouncementSources[0].Id);
        Assert.True(merged.AnnouncementSources[0].Enabled);
    }
}
