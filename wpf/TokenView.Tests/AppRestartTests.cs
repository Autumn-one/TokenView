using TokenView.Wpf;
using Xunit;

namespace TokenView.Tests;

public sealed class AppRestartTests
{
    [Theory]
    [InlineData(new[] { "--restart-wait", "1234" }, 1234)]
    [InlineData(new[] { "--hidden", "--restart-wait", "42" }, 42)]
    [InlineData(new[] { "--RESTART-WAIT", "7" }, 7)]
    public void ParsesRestartProcessId(string[] args, int expected)
    {
        Assert.Equal(expected, App.ParseRestartPid(args));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void IgnoresInvalidProcessId(string value)
    {
        Assert.Null(App.ParseRestartPid(["--restart-wait", value]));
    }

    [Fact]
    public void IgnoresMissingProcessId()
    {
        Assert.Null(App.ParseRestartPid(["--restart-wait"]));
        Assert.Null(App.ParseRestartPid(["--hidden"]));
    }
}
