using System.Threading;
using System.Windows.Documents;
using TokenView.Wpf.Controls;
using Xunit;

namespace TokenView.Tests;

public sealed class MarkdownViewTests
{
    [Fact]
    public void RendersTablesLinksAndRawHtml()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new MarkdownView
                {
                    Markdown = """
                        # Title

                        | A | B |
                        |---|---|
                        | 1 | 2 |

                        [site](https://example.com) [relative](docs/a.md)

                        **bold** *italic*

                        > quoted

                        <div>raw html</div>

                        ![alt](https://example.com/a.png)
                        """,
                };

                var document = view.Document;
                var table = Assert.Single(document.Blocks.OfType<Table>());
                Assert.Equal(2, table.RowGroups[0].Rows.Count);
                Assert.Equal(2, table.RowGroups[0].Rows[0].Cells.Count);

                var hyperlinks = EnumerateInlines(document)
                    .OfType<Hyperlink>()
                    .ToList();
                Assert.Equal(2, hyperlinks.Count);
                Assert.All(hyperlinks, link => Assert.NotNull(link.NavigateUri));
                Assert.All(hyperlinks, link =>
                    Assert.Contains(link.NavigateUri!.Scheme, new[] { "http", "https", "mailto" }));

                var inlines = EnumerateInlines(document).ToList();
                Assert.Contains(inlines, inline => inline is Bold);
                Assert.Contains(inlines, inline => inline is Italic);
                Assert.Contains(document.Blocks, block => block is Section);

                var text = Flatten(document);
                Assert.Contains("raw html", text);
                Assert.Contains("relative", text);
                Assert.Contains("alt", text);
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static IEnumerable<Inline> EnumerateInlines(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        {
            foreach (var inline in EnumerateInlines(block))
            {
                yield return inline;
            }
        }
    }

    private static IEnumerable<Inline> EnumerateInlines(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                foreach (var inline in EnumerateInlines(paragraph.Inlines))
                {
                    yield return inline;
                }
                break;
            case Section section:
                foreach (var child in section.Blocks)
                {
                    foreach (var inline in EnumerateInlines(child))
                    {
                        yield return inline;
                    }
                }
                break;
            case List list:
                foreach (var item in list.ListItems)
                {
                    foreach (var child in item.Blocks)
                    {
                        foreach (var inline in EnumerateInlines(child))
                        {
                            yield return inline;
                        }
                    }
                }
                break;
            case Table table:
                foreach (var group in table.RowGroups)
                {
                    foreach (var row in group.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var child in cell.Blocks)
                            {
                                foreach (var inline in EnumerateInlines(child))
                                {
                                    yield return inline;
                                }
                            }
                        }
                    }
                }
                break;
        }
    }

    private static IEnumerable<Inline> EnumerateInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
            {
                foreach (var nested in EnumerateInlines(span.Inlines))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string Flatten(FlowDocument document)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var block in document.Blocks)
        {
            AppendBlock(builder, block);
        }
        return builder.ToString();
    }

    private static void AppendBlock(System.Text.StringBuilder builder, Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                builder.Append(string.Concat(paragraph.Inlines.OfType<Run>().Select(run => run.Text)));
                builder.Append('\n');
                foreach (var span in paragraph.Inlines.OfType<Span>())
                {
                    builder.Append(string.Concat(span.Inlines.OfType<Run>().Select(run => run.Text)));
                }
                break;
            case Section section:
                foreach (var child in section.Blocks)
                {
                    AppendBlock(builder, child);
                }
                break;
            case Table table:
                foreach (var group in table.RowGroups)
                {
                    foreach (var row in group.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var child in cell.Blocks)
                            {
                                AppendBlock(builder, child);
                            }
                        }
                    }
                }
                break;
        }
    }
}
