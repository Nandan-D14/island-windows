using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Island;

// Tiny markdown renderer for chat replies and notes (no HTML, no remote images):
// # headings, **bold**, *italic*, ~~strike~~, `code`, ``` blocks, - lists, 1. lists, - [ ] checkboxes, > quotes, ---, [links](https://...)
static class Md
{
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    static readonly SolidColorBrush DimText = new(Color.FromArgb(0x8C, 255, 255, 255));
    static readonly SolidColorBrush CodeBg = new(Color.FromArgb(0x30, 255, 255, 255));
    static readonly SolidColorBrush BlockBg = new(Color.FromArgb(0x48, 0, 0, 0));
    static readonly SolidColorBrush LinkFg = new(Color.FromRgb(0x6C, 0xB4, 0xFF));
    static readonly SolidColorBrush QuoteBar = new(Color.FromArgb(0x50, 255, 255, 255));

    static readonly Regex Heading = new(@"^(#{1,4})\s+(.*)$"), Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$"),
        Check = new(@"^(\s*)[-*+]\s+\[([ xX])\]\s?(.*)$"), Bullet = new(@"^(\s*)[-*+\u2022]\s+(.*)$"),
        Number = new(@"^(\s*)(\d+)[.)]\s+(.*)$"), Quote = new(@"^\s*>\s?(.*)$"),
        TableSep = new(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?\s*$|^\s*\|\s*:?-+:?\s*\|\s*$");
    static readonly Regex InlineRx = new(@"(`[^`\n]+`)|(\*\*[^*\n]+\*\*|__[^_\n]+__)|(~~[^~\n]+~~)|(\*[^*\s][^*\n]*\*)|(\[[^\]\n]+\]\((https?://[^)\s]+)\))");

    // onCheck(lineIndex) is called when a checkbox is clicked (notes); lead = inline put before the first line (chat "Gemini" label)
    public static FrameworkElement Render(string md, double size = 13, Action<int>? onCheck = null, Inline? lead = null)
    {
        var root = new StackPanel();
        var lines = md.Replace("\r", "").Split('\n');
        TextBlock? para = null;

        TextBlock NewText(double fs, FontWeight? w = null)
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = fs, Foreground = Brushes.White, FontWeight = w ?? FontWeights.Normal };
            if (lead != null) { t.Inlines.Add(lead); lead = null; }
            return t;
        }
        void Add(FrameworkElement e, double below = 4) { e.Margin = new Thickness(e.Margin.Left, e.Margin.Top, 0, below); root.Children.Add(e); }
        void EndPara() { para = null; }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            Match m;

            // fenced code block
            if (line.TrimStart().StartsWith("```"))
            {
                EndPara();
                var code = new List<string>();
                while (++i < lines.Length && !lines[i].TrimStart().StartsWith("```")) code.Add(lines[i]);
                if (lead != null) Add(NewText(size), 2);
                var tb = new TextBlock { Text = string.Join("\n", code), FontFamily = Mono, FontSize = size - 1.5, Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEA)), TextWrapping = TextWrapping.Wrap };
                Add(new Border { Background = BlockBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Child = tb }, 6);
                continue;
            }
            if (line.Trim() == "") { EndPara(); continue; }
            // | table | with | a |---| line under the header
            if (line.Contains('|') && i + 1 < lines.Length && TableSep.IsMatch(lines[i + 1]))
            {
                EndPara();
                var head = Cells(line);
                var aligns = Cells(lines[i + 1]).Select(c => c.StartsWith(':') && c.EndsWith(':') ? TextAlignment.Center : c.EndsWith(':') ? TextAlignment.Right : TextAlignment.Left).ToArray();
                var rows = new List<string[]>();
                for (i += 2; i < lines.Length && lines[i].Contains('|') && lines[i].Trim() != ""; i++) rows.Add(Cells(lines[i]));
                i--; // the for loop moves on
                if (lead != null) Add(NewText(size), 2);
                Add(Table(head, aligns, rows, size), 8);
                continue;
            }
            if (Rule.IsMatch(line)) { EndPara(); Add(new Border { Height = 1, Background = QuoteBar, Margin = new Thickness(0, 4, 0, 0) }, 6); continue; }
            if ((m = Heading.Match(line)).Success)
            {
                EndPara();
                int lvl = m.Groups[1].Length;
                var h = NewText(lvl switch { 1 => size + 5, 2 => size + 3, 3 => size + 1.5, _ => size + 0.5 }, FontWeights.SemiBold);
                Inlines(h.Inlines, m.Groups[2].Value);
                h.Margin = new Thickness(0, root.Children.Count > 0 ? 6 : 0, 0, 0);
                Add(h, 4);
                continue;
            }
            if ((m = Check.Match(line)).Success)
            {
                EndPara();
                bool done = m.Groups[2].Value != " ";
                int idx = i;
                var box = new TextBlock { Text = done ? "\u2611" : "\u2610", Tag = "md-check", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = size + 2, Margin = new Thickness(0, -2, 8, 0),
                                          Foreground = done ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) : Brushes.White };
                if (onCheck != null) { box.Cursor = Cursors.Hand; box.MouseLeftButtonDown += (_, e) => { onCheck(idx); e.Handled = true; }; }
                var t = NewText(size);
                Inlines(t.Inlines, m.Groups[3].Value);
                if (done) { t.Foreground = DimText; t.TextDecorations = TextDecorations.Strikethrough; }
                Add(Item(box, t, Indent(m.Groups[1].Value)), 3);
                continue;
            }
            if ((m = Bullet.Match(line)).Success)
            {
                EndPara();
                int ind = Indent(m.Groups[1].Value);
                var dot = new TextBlock { Text = ind > 0 ? "\u25E6" : "\u2022", FontSize = size, Foreground = DimText, Margin = new Thickness(2, 0, 8, 0) };
                var t = NewText(size); Inlines(t.Inlines, m.Groups[2].Value);
                Add(Item(dot, t, ind), 3);
                continue;
            }
            if ((m = Number.Match(line)).Success)
            {
                EndPara();
                var num = new TextBlock { Text = m.Groups[2].Value + ".", FontSize = size, Foreground = DimText, MinWidth = 16, Margin = new Thickness(0, 0, 6, 0) };
                var t = NewText(size); Inlines(t.Inlines, m.Groups[3].Value);
                Add(Item(num, t, Indent(m.Groups[1].Value)), 3);
                continue;
            }
            if ((m = Quote.Match(line)).Success)
            {
                EndPara();
                var t = NewText(size); t.Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255)); Inlines(t.Inlines, m.Groups[1].Value);
                Add(new Border { BorderBrush = QuoteBar, BorderThickness = new Thickness(2.5, 0, 0, 0), Padding = new Thickness(10, 1, 0, 1), Child = t }, 4);
                continue;
            }
            // plain text: consecutive lines stay together, each on its own line
            if (para == null) { para = NewText(size); Add(para, 6); }
            else para.Inlines.Add(new LineBreak());
            Inlines(para.Inlines, line);
        }
        if (lead != null) Add(NewText(size), 0); // empty text: still show the label
        if (root.Children.Count > 0 && root.Children[^1] is FrameworkElement last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, 0, 0);
        return root;
    }

    static int Indent(string ws) => Math.Min(3, ws.Replace("\t", "    ").Length / 2);

    // "| a | b \| c |" -> ["a", "b | c"]
    static string[] Cells(string row)
    {
        var r = row.Trim().Replace("\\|", "\u0001");
        if (r.StartsWith('|')) r = r[1..];
        if (r.EndsWith('|')) r = r[..^1];
        return r.Split('|').Select(c => c.Trim().Replace('\u0001', '|')).ToArray();
    }

    static readonly SolidColorBrush TableLine = new(Color.FromArgb(0x1C, 255, 255, 255)), TableHead = new(Color.FromArgb(0x1E, 255, 255, 255)),
                                    TableBg = new(Color.FromArgb(0x24, 0, 0, 0));

    static FrameworkElement Table(string[] head, TextAlignment[] aligns, List<string[]> rows, double size)
    {
        int n = head.Length;
        var g = new Grid();
        // column widths follow how much text each column has (wraps instead of overflowing)
        for (int c = 0; c < n; c++)
        {
            var lens = rows.Select(r => c < r.Length ? r[c].Length : 0).Append(head[c].Length).ToList();
            double w = Math.Clamp(lens.Average() * 0.6 + lens.Max() * 0.4, 4, 60);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        }
        void Row(string[] cells, int r, bool header)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < n; c++)
            {
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size - 1, Foreground = Brushes.White, TextAlignment = c < aligns.Length ? aligns[c] : TextAlignment.Left };
                if (header) t.FontWeight = FontWeights.SemiBold;
                Inlines(t.Inlines, c < cells.Length ? cells[c] : "");
                var cell = new Border
                {
                    Child = t, Padding = new Thickness(8, 5, 8, 5), Background = header ? TableHead : Brushes.Transparent,
                    BorderBrush = TableLine, BorderThickness = new Thickness(c > 0 ? 1 : 0, r > 0 ? 1 : 0, 0, 0)
                };
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c);
                g.Children.Add(cell);
            }
        }
        Row(head, 0, true);
        for (int r = 0; r < rows.Count; r++) Row(rows[r], r + 1, false);
        var frame = new Border { CornerRadius = new CornerRadius(8), BorderBrush = TableLine, BorderThickness = new Thickness(1), Background = TableBg, Child = g };
        // round the inner corners too
        frame.Loaded += (_, _) => g.Clip = new RectangleGeometry(new Rect(0, 0, g.ActualWidth, g.ActualHeight), 7, 7);
        g.SizeChanged += (_, _) => g.Clip = new RectangleGeometry(new Rect(0, 0, g.ActualWidth, g.ActualHeight), 7, 7);
        return frame;
    }

    static Grid Item(FrameworkElement marker, TextBlock text, int indent)
    {
        var g = new Grid { Margin = new Thickness(indent * 14, 0, 0, 0), ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        g.Children.Add(marker); g.Children.Add(text); Grid.SetColumn(text, 1);
        return g;
    }

    // **bold**, *italic*, ~~strike~~, `code`, [link](https://...)
    static void Inlines(InlineCollection into, string s)
    {
        int at = 0;
        foreach (Match m in InlineRx.Matches(s))
        {
            if (m.Index > at) into.Add(new Run(s[at..m.Index]));
            var v = m.Value;
            if (m.Groups[1].Success)
                into.Add(new Run(v[1..^1]) { FontFamily = Mono, Background = CodeBg, FontSize = 12 });
            else if (m.Groups[2].Success) { var b = new Bold(); Inlines(b.Inlines, v[2..^2]); into.Add(b); }
            else if (m.Groups[3].Success) { var st = new Span { TextDecorations = TextDecorations.Strikethrough }; Inlines(st.Inlines, v[2..^2]); into.Add(st); }
            else if (m.Groups[4].Success) { var it = new Italic(); Inlines(it.Inlines, v[1..^1]); into.Add(it); }
            else if (m.Groups[5].Success)
            {
                var text = v[1..v.IndexOf("](")];
                var url = m.Groups[6].Value;
                var link = new Hyperlink(new Run(text)) { Foreground = LinkFg, TextDecorations = null, Cursor = Cursors.Hand };
                link.Click += (_, _) =>
                {
                    // only real web links (the regex already requires http/https)
                    if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
                };
                into.Add(link);
            }
            at = m.Index + m.Length;
        }
        if (at < s.Length) into.Add(new Run(s[at..]));
    }
}
