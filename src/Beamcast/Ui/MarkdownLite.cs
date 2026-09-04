using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Beamcast;

/// <summary>Renders changelog Markdown: headings, bullets, bold, code and links.</summary>
internal static partial class MarkdownLite
{
    public static void Render(RichTextBlock target, string markdown)
    {
        target.Blocks.Clear();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
                target.Blocks.Add(new Paragraph { FontSize = 6 });
            else if (line.StartsWith("### "))
                target.Blocks.Add(Heading(line[4..], 15, 10));
            else if (line.StartsWith("## "))
                target.Blocks.Add(Heading(line[3..], 20, 14));
            else if (line.StartsWith("# "))
                target.Blocks.Add(Heading(line[2..], 24, 2));
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                target.Blocks.Add(Bullet(line[2..]));
            else
                target.Blocks.Add(Body(line));
        }
    }

    private static Paragraph Heading(string text, double size, double top)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, top, 0, 4) };
        paragraph.Inlines.Add(
            new Run
            {
                Text = StripMarks(text),
                FontSize = size,
                FontWeight = FontWeights.SemiBold,
            }
        );
        return paragraph;
    }

    private static Paragraph Bullet(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(8, 2, 0, 2), TextIndent = -14 };
        paragraph.Inlines.Add(new Run { Text = "•  " });
        AddInlines(paragraph.Inlines, text);
        return paragraph;
    }

    private static Paragraph Body(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
        AddInlines(paragraph.Inlines, text);
        return paragraph;
    }

    private static void AddInlines(InlineCollection target, string text)
    {
        var pos = 0;
        foreach (Match match in InlineRegex().Matches(text))
        {
            if (match.Index > pos)
                target.Add(new Run { Text = text[pos..match.Index] });
            AppendMatch(target, match);
            pos = match.Index + match.Length;
        }

        if (pos < text.Length)
            target.Add(new Run { Text = text[pos..] });
    }

    private static void AppendMatch(InlineCollection target, Match match)
    {
        if (match.Groups["lt"].Success)
        {
            var link = new Hyperlink();
            link.Inlines.Add(new Run { Text = match.Groups["lt"].Value });
            var url = match.Groups["lu"].Value;
            link.Click += async (_, _) => await SafeUrl.OpenAsync(url);
            target.Add(link);
            return;
        }

        if (match.Groups["b"].Success)
        {
            target.Add(
                new Run { Text = match.Groups["b"].Value, FontWeight = FontWeights.SemiBold }
            );
            return;
        }

        if (match.Groups["c"].Success)
            target.Add(
                new Run
                {
                    Text = match.Groups["c"].Value,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                }
            );
    }

    private static string StripMarks(string value) => value.Replace("**", "").Replace("`", "");

    [GeneratedRegex(@"\[(?<lt>[^\]]+)\]\((?<lu>[^)]+)\)|\*\*(?<b>[^*]+)\*\*|`(?<c>[^`]+)`")]
    private static partial Regex InlineRegex();
}
