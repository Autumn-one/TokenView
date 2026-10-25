using System.Security;
using TokenView.Wpf.Services;
using Xunit;

namespace TokenView.Tests;

public sealed class QoderUsageRecordingServiceTests
{
    [Fact]
    public void EnablesBothEditionsAndBothScopesWithoutMachineWrites()
    {
        var values = new Dictionary<(string, EnvironmentVariableTarget), string>();
        var notifications = 0;
        string? Apply() => QoderUsageRecordingService.EnsureEnabled(["qoder"],
            (name, scope) => values.GetValueOrDefault((name, scope)),
            (name, value, scope) => values[(name, scope)] = value,
            () => notifications++);

        Assert.Null(Apply());
        Assert.Equal(4, values.Count);
        foreach (var name in new[] { "QODER_EXPOSE_TOKEN_USAGE", "QODERCN_EXPOSE_TOKEN_USAGE" })
        {
            Assert.Equal("true", values[(name, EnvironmentVariableTarget.User)]);
            Assert.Equal("true", values[(name, EnvironmentVariableTarget.Process)]);
            Assert.False(values.ContainsKey((name, EnvironmentVariableTarget.Machine)));
        }
        Assert.Equal(1, notifications);
        Assert.Null(Apply());
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void DisabledCollectorDoesNotReadOrWriteEnvironment()
    {
        var error = QoderUsageRecordingService.EnsureEnabled(["codex"],
            (_, _) => throw new InvalidOperationException("must not read"),
            (_, _, _) => throw new InvalidOperationException("must not write"),
            () => throw new InvalidOperationException("must not notify"));

        Assert.Null(error);
    }

    [Theory]
    [InlineData("1")]
    [InlineData(" TRUE ")]
    [InlineData("yes")]
    public void ExistingTruthyValuesDoNotCauseWritesOrBroadcasts(string existing)
    {
        var error = QoderUsageRecordingService.EnsureEnabled(["qoder"],
            (_, _) => existing,
            (_, _, _) => throw new InvalidOperationException("must not write"),
            () => throw new InvalidOperationException("must not notify"));

        Assert.Null(error);
    }

    [Fact]
    public void UserScopeFailureStillEnablesOtherEditionAndProcessAndReturnsSanitizedError()
    {
        var writes = new List<(string Name, EnvironmentVariableTarget Scope)>();
        var notifications = 0;
        var error = QoderUsageRecordingService.EnsureEnabled(["qoder"], (_, _) => "false",
            (name, _, scope) =>
            {
                if (name == "QODER_EXPOSE_TOKEN_USAGE" && scope == EnvironmentVariableTarget.User)
                {
                    throw new SecurityException("private fixture path");
                }
                writes.Add((name, scope));
            }, () => notifications++);

        Assert.NotNull(error);
        Assert.DoesNotContain("private fixture path", error);
        Assert.Equal(3, writes.Count);
        Assert.Contains(("QODERCN_EXPOSE_TOKEN_USAGE", EnvironmentVariableTarget.User), writes);
        Assert.Equal(1, notifications);
    }
}
