using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace TokenView.Wpf.Controls;

/// <summary>把 Markdown 渲染为 FlowDocument：HTML 只当文本，图片只显示为链接，不加载远程资源。</summary>
public sealed class MarkdownView : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Microsoft YaHei UI");

    public MarkdownView()
    {
        IsToolBarVisible = false;
        IsSelectionEnabled = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = BuildDocument(string.Empty);
    }

    public string Markdown
    {
        get => GetValue(MarkdownProperty) as string ?? string.Empty;
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownView)d).Document = BuildDocument(e.NewValue as string ?? string.Empty);

    private static FlowDocument BuildDocument(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            LineHeight = 21,
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Left,
        };
        document.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

        var parsed = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var block in parsed)
        {
            AddBlock(document.Blocks, block, document);
        }
        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph());
        }
        return document;
    }

    private static void AddBlock(BlockCollection target, MdBlock block, FlowDocument document)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var paragraph = new Paragraph
                {
                    FontSize = heading.Level switch
                    {
                        1 => 22,
                        2 => 18,
                        3 => 16,
                        4 => 14.5,
                        5 => 13.5,
                        _ => 13,
                    },
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, heading.Level <= 2 ? 18 : 13, 0, 6),
                };
                AddInlines(paragraph.Inlines, heading.Inline);
                target.Add(paragraph);
                break;
            }
            case ParagraphBlock paragraphBlock:
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                AddInlines(paragraph.Inlines, paragraphBlock.Inline);
                target.Add(paragraph);
                break;
            }
            case ListBlock listBlock:
            {
                var list = new WpfList
                {
                    MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 2, 0, 10),
                    Padding = new Thickness(20, 0, 0, 0),
                };
                if (listBlock.IsOrdered && int.TryParse(listBlock.OrderedStart, out var start) && start > 1)
                {
                    list.StartIndex = start;
                }
                foreach (var itemBlock in listBlock.OfType<ListItemBlock>())
                {
                    var item = new ListItem();
                    foreach (var child in itemBlock)
                    {
                        AddBlock(item.Blocks, child, document);
                    }
                    list.ListItems.Add(item);
                }
                target.Add(list);
                break;
            }
            case QuoteBlock quote:
            {
                var section = new Section
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 2, 0, 0),
                    Margin = new Thickness(0, 4, 0, 12),
                };
                section.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
                section.SetResourceReference(WpfBlock.BorderBrushProperty, "BorderBrush");
                foreach (var child in quote)
                {
                    AddBlock(section.Blocks, child, document);
                }
                target.Add(section);
                break;
            }
            case CodeBlock code:
            {
                var paragraph = new Paragraph
                {
                    FontFamily = MonoFont,
                    FontSize = 12,
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 4, 0, 12),
                };
                paragraph.SetResourceReference(TextElement.BackgroundProperty, "ChipBrush");
                AddPreservedText(paragraph.Inlines, code.Lines.ToString());
                target.Add(paragraph);
                break;
            }
            case HtmlBlock html:
            {
                var paragraph = new Paragraph(new Run(html.Lines.ToString()))
                {
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 10),
                };
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
                target.Add(paragraph);
                break;
            }
            case ThematicBreakBlock:
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 12) };
                line.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
                target.Add(new BlockUIContainer(line));
                break;
            }
            case MdTable table:
                AddTable(target, table, document);
                break;
            case ContainerBlock container:
                foreach (var child in container)
                {
                    AddBlock(target, child, document);
                }
                break;
        }
    }

    private static void AddTable(BlockCollection target, MdTable source, FlowDocument document)
    {
        var table = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 12),
        };
        var columnCount = Math.Max(1, source.ColumnDefinitions.Count);
        foreach (var sourceRow in source.OfType<MdTableRow>())
        {
            columnCount = Math.Max(columnCount, sourceRow.OfType<MdTableCell>().Count());
        }
        for (var index = 0; index < columnCount; index++)
        {
            table.Columns.Add(new TableColumn());
        }

        var rowGroup = new TableRowGroup();
        table.RowGroups.Add(rowGroup);
        foreach (var sourceRow in source.OfType<MdTableRow>())
        {
            var row = new WpfTableRow();
            if (sourceRow.IsHeader)
            {
                row.FontWeight = FontWeights.SemiBold;
            }

            foreach (var sourceCell in sourceRow.OfType<MdTableCell>())
            {
                var cell = new WpfTableCell
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    BorderThickness = new Thickness(1),
                };
                cell.SetResourceReference(WpfTableCell.BorderBrushProperty, "BorderBrush");
                if (sourceRow.IsHeader)
                {
                    cell.SetResourceReference(TextElement.BackgroundProperty, "ChipBrush");
                }
                foreach (var child in sourceCell)
                {
                    AddBlock(cell.Blocks, child, document);
                }
                if (cell.Blocks.Count == 0)
                {
                    cell.Blocks.Add(new Paragraph());
                }
                row.Cells.Add(cell);
            }
            rowGroup.Rows.Add(row);
        }
        target.Add(table);
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }
        foreach (var inline in container)
        {
            AddInline(target, inline);
        }
    }

    private static void AddInline(InlineCollection target, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;
            case LineBreakInline:
                target.Add(new LineBreak());
                break;
            case EmphasisInline emphasis:
            {
                Span span = emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2
                    ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2
                        ? new Bold()
                        : new Italic();
                AddInlines(span.Inlines, emphasis);
                target.Add(span);
                break;
            }
            case CodeInline code:
            {
                var run = new Run(code.Content)
                {
                    FontFamily = MonoFont,
                    FontSize = 12,
                };
                run.SetResourceReference(TextElement.BackgroundProperty, "ChipBrush");
                target.Add(run);
                break;
            }
            case LinkInline link:
                AddLink(target, link);
                break;
            case AutolinkInline autolink:
                if (TrySafeUri(autolink.Url, out var autolinkUri))
                {
                    AddHyperlink(target, autolinkUri, autolink.Url, autolink.Url);
                }
                else
                {
                    target.Add(new Run(autolink.Url));
                }
                break;
            case HtmlInline html:
                target.Add(new Run(html.Tag));
                break;
            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;
            case ContainerInline container:
                AddInlines(target, container);
                break;
        }
    }

    private static void AddLink(InlineCollection target, LinkInline link)
    {
        var label = FlattenText(link);
        if (link.IsImage)
        {
            if (TrySafeUri(link.Url, out var imageUri))
            {
                AddHyperlink(target, imageUri,
                    string.IsNullOrWhiteSpace(label) ? imageUri.AbsoluteUri : label,
                    string.IsNullOrWhiteSpace(label) ? imageUri.AbsoluteUri : label);
            }
            else
            {
                target.Add(new Run(string.IsNullOrWhiteSpace(label) ? link.Url ?? "" : label));
            }
            return;
        }

        if (TrySafeUri(link.Url, out var linkUri))
        {
            AddHyperlink(target, linkUri,
                string.IsNullOrWhiteSpace(label) ? linkUri.AbsoluteUri : label,
                string.IsNullOrWhiteSpace(label) ? linkUri.AbsoluteUri : label);
            return;
        }
        AddInlines(target, link);
    }

    private static void AddHyperlink(InlineCollection target, Uri uri, string text, string tooltip)
    {
        var hyperlink = new Hyperlink
        {
            NavigateUri = uri,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
        };
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentDarkBrush");
        hyperlink.Inlines.Add(new Run(text));
        hyperlink.RequestNavigate += OnRequestNavigate;
        target.Add(hyperlink);
    }

    private static string FlattenText(ContainerInline container)
    {
        var builder = new StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case HtmlEntityInline entity:
                    builder.Append(entity.Transcoded.ToString());
                    break;
                case ContainerInline nested:
                    builder.Append(FlattenText(nested));
                    break;
            }
        }
        return builder.ToString().Trim();
    }

    private static void AddPreservedText(InlineCollection target, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                target.Add(new LineBreak());
            }
            target.Add(new Run(lines[index]));
        }
    }

    private static bool TrySafeUri(string? value, out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp ||
             parsed.Scheme == Uri.UriSchemeHttps ||
             parsed.Scheme == Uri.UriSchemeMailto))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri is null || !TrySafeUri(e.Uri.AbsoluteUri, out var uri))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"无法打开链接：{uri.AbsoluteUri}", "TokenView",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
