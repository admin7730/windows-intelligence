using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;

namespace WindowsIntelligence;

internal static class ReplyMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseAutoLinks().DisableHtml().Build();
    private static readonly FontFamily Mono = Ui.Mono;

    public static FlowDocument Format(string text, bool light = false)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), Foreground = light ? Ui.Brush("#222534") : Ui.Ink, FontSize = 14, FontFamily = Ui.Body };
        AddBlocks(doc.Blocks, Markdown.Parse(text, Pipeline), light);
        return doc;
    }

    private static void AddBlocks(BlockCollection output, ContainerBlock source, bool light)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var h = Paragraph(heading.Inline, light);
                    h.FontWeight = FontWeights.SemiBold;
                    h.FontSize = heading.Level <= 2 ? 19 : 16;
                    h.Margin = new Thickness(0, 5, 0, 8);
                    output.Add(h);
                    break;
                case ParagraphBlock paragraph:
                    output.Add(Paragraph(paragraph.Inline, light));
                    break;
                case CodeBlock code:
                    var p = new Paragraph(new Run(code.Lines.ToString().TrimEnd('\r', '\n')))
                    {
                        FontFamily = Mono, FontSize = 12, Background = light ? Ui.Brush("#40FFFFFF") : Ui.Field,
                        Padding = new Thickness(9), Margin = new Thickness(0, 3, 0, 9)
                    };
                    output.Add(p);
                    break;
                case ListBlock list:
                    var items = new System.Windows.Documents.List
                    {
                        MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        StartIndex = int.TryParse(list.OrderedStart, out var start) ? Math.Max(1, start) : 1,
                        Margin = new Thickness(0, 0, 0, 7)
                    };
                    foreach (var entry in list.OfType<ListItemBlock>())
                    {
                        var item = new ListItem();
                        AddBlocks(item.Blocks, entry, light);
                        items.ListItems.Add(item);
                    }
                    output.Add(items);
                    break;
                case QuoteBlock quote:
                    var section = new Section
                    {
                        BorderBrush = Ui.Line, BorderThickness = new Thickness(2, 0, 0, 0),
                        Padding = new Thickness(9, 1, 0, 1), Foreground = light ? Ui.Brush("#5F6478") : Ui.Muted
                    };
                    AddBlocks(section.Blocks, quote, light);
                    output.Add(section);
                    break;
                case MdTable table:
                    var formatted = new Table { CellSpacing = 0, FontSize = 13 };
                    var rows = new TableRowGroup();
                    foreach (var row in table.OfType<MdRow>())
                    {
                        var line = new TableRow();
                        foreach (var cell in row.OfType<MdCell>())
                        {
                            var column = new TableCell
                            {
                                BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 1),
                                Padding = new Thickness(6, 4, 6, 1),
                                Background = row.IsHeader ? (light ? Ui.Brush("#68FFFFFF") : Ui.Panel) : Brushes.Transparent,
                                FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal
                            };
                            AddBlocks(column.Blocks, cell, light);
                            line.Cells.Add(column);
                        }
                        rows.Rows.Add(line);
                    }
                    formatted.RowGroups.Add(rows);
                    output.Add(formatted);
                    break;
                case ThematicBreakBlock:
                    output.Add(new Paragraph { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 2 });
                    break;
                case ContainerBlock nested:
                    AddBlocks(output, nested, light);
                    break;
            }
        }
    }

    private static Paragraph Paragraph(ContainerInline? content, bool light)
    {
        var result = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
        if (content is not null) AddInlines(result.Inlines, content, light);
        return result;
    }

    private static void AddInlines(InlineCollection output, ContainerInline source, bool light)
    {
        foreach (var item in source)
        {
            switch (item)
            {
                case LiteralInline text: output.Add(new Run(text.Content.ToString())); break;
                case CodeInline code: output.Add(new Run(code.Content) { FontFamily = Mono, Background = light ? Ui.Brush("#40FFFFFF") : Ui.Field }); break;
                case LineBreakInline line: output.Add(line.IsHard ? new LineBreak() : new Run(" ")); break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis, light); output.Add(span);
                    break;
                case LinkInline link when !link.IsImage && WebUri(link.Url) is { } uri:
                    var hyperlink = WebLink(uri, light);
                    AddInlines(hyperlink.Inlines, link, light);
                    output.Add(hyperlink);
                    break;
                case AutolinkInline auto when WebUri(auto.Url) is { } url:
                    var direct = WebLink(url, light); direct.Inlines.Add(new Run(auto.Url)); output.Add(direct); break;
                case ContainerInline nested:
                    AddInlines(output, nested, light);
                    break;
                case HtmlEntityInline entity: output.Add(new Run(entity.Transcoded.ToString())); break;
            }
        }
    }

    private static Uri? WebUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" ? uri : null;

    private static Hyperlink WebLink(Uri uri, bool light)
    {
        var link = new Hyperlink { NavigateUri = uri, Foreground = light ? Ui.Brush("#0057B8") : Ui.Accent, ToolTip = uri.AbsoluteUri };
        link.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { }
        };
        return link;
    }
}

internal sealed class MarkdownReplyView : RichTextBox
{
    private readonly DispatcherTimer refresh;
    private string source = "", rendered = "";
    public bool Light { get; init; }
    public MarkdownReplyView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
        refresh.Tick += (_, _) => Flush();
        Unloaded += (_, _) => refresh.Stop();
        Loaded += (_, _) => Flush();
    }

    public void SetMarkdown(string text, bool immediate = false)
    {
        source = text;
        if (immediate || text.Length == 0) Flush();
        else if (source != rendered && !refresh.IsEnabled) refresh.Start();
    }

    private void Flush()
    {
        refresh.Stop();
        if (source == rendered) return;
        double offset = VerticalOffset;
        Document = ReplyMarkdown.Format(source, Light);
        rendered = source;
        ScrollToVerticalOffset(offset);
    }
}
