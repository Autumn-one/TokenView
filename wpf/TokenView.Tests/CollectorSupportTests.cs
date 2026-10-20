using System.IO;
using TokenView.Wpf.Data.Collectors;
using Xunit;

namespace TokenView.Tests;

public sealed class CollectorSupportTests
{
    [Fact]
    public void ReadsLinesAcrossBufferBoundariesAndReportsCompleteness()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tokenview-jsonl-{Guid.NewGuid():N}.jsonl");
        var longLine = new string('x', 200_000);
        File.WriteAllText(path, "first\n" + longLine + "\n" + "crlf\r\n" + "\n" + "last");
        try
        {
            var lines = new List<(string Text, bool Complete)>();
            CollectorSupport.ReadJsonLines(path, (text, complete) => lines.Add((text, complete)));

            Assert.Equal(5, lines.Count);
            Assert.Equal("first", lines[0].Text);
            Assert.Equal(longLine, lines[1].Text);
            Assert.Equal("crlf", lines[2].Text);
            Assert.Equal("", lines[3].Text);
            Assert.Equal("last", lines[4].Text);
            Assert.All(lines.Take(4), line => Assert.True(line.Complete));
            Assert.False(lines[4].Complete);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsEmptyFileWithoutCallbacks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tokenview-jsonl-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, "");
        try
        {
            var count = 0;
            CollectorSupport.ReadJsonLines(path, (_, _) => count++);

            Assert.Equal(0, count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsTrailingNewlineAsCompleteEmptyLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tokenview-jsonl-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, "only\n");
        try
        {
            var lines = new List<(string Text, bool Complete)>();
            CollectorSupport.ReadJsonLines(path, (text, complete) => lines.Add((text, complete)));

            var line = Assert.Single(lines);
            Assert.Equal("only", line.Text);
            Assert.True(line.Complete);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
