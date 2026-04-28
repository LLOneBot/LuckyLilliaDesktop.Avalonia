using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LuckyLilliaDesktop.Utils;

public static partial class AnsiParser
{
    public static readonly AttachedProperty<string?> AnsiTextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("AnsiText", typeof(AnsiParser));

    static AnsiParser()
    {
        AnsiTextProperty.Changed.AddClassHandler<TextBlock>(OnAnsiTextChanged);
    }

    public static string? GetAnsiText(TextBlock textBlock) => textBlock.GetValue(AnsiTextProperty);
    public static void SetAnsiText(TextBlock textBlock, string? value) => textBlock.SetValue(AnsiTextProperty, value);

    private static void OnAnsiTextChanged(TextBlock textBlock, AvaloniaPropertyChangedEventArgs e)
    {
        textBlock.Inlines?.Clear();
        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text))
            return;

        textBlock.Inlines ??= new InlineCollection();
        foreach (var segment in ParseAnsi(text))
        {
            var run = new Run(segment.Text);
            if (segment.Foreground != null)
                run.Foreground = segment.Foreground;
            textBlock.Inlines.Add(run);
        }
    }

    private static readonly Regex AnsiRegex = CreateAnsiRegex();

    [GeneratedRegex(@"\x1B\[([0-9;]*)m")]
    private static partial Regex CreateAnsiRegex();

    private static readonly IBrush[] Basic16 =
    {
        Brush.Parse("#4E4E4E"), // 0 black
        Brush.Parse("#CD3131"), // 1 red
        Brush.Parse("#0DBC79"), // 2 green
        Brush.Parse("#E5E510"), // 3 yellow
        Brush.Parse("#2472C8"), // 4 blue
        Brush.Parse("#BC3FBC"), // 5 magenta
        Brush.Parse("#11A8CD"), // 6 cyan
        Brush.Parse("#E5E5E5"), // 7 white
        Brush.Parse("#666666"), // 8 bright black (gray)
        Brush.Parse("#F14C4C"), // 9 bright red
        Brush.Parse("#23D18B"), // 10 bright green
        Brush.Parse("#F5F543"), // 11 bright yellow
        Brush.Parse("#3B8EEA"), // 12 bright blue
        Brush.Parse("#D670D6"), // 13 bright magenta
        Brush.Parse("#29B8DB"), // 14 bright cyan
        Brush.Parse("#FFFFFF"), // 15 bright white
    };

    private static IBrush? Color256ToBrush(int index)
    {
        if (index < 16)
            return Basic16[index];

        if (index < 232)
        {
            index -= 16;
            var r = index / 36 * 51;
            var g = index / 6 % 6 * 51;
            var b = index % 6 * 51;
            return new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
        }

        // grayscale 232-255
        var gray = (byte)((index - 232) * 10 + 8);
        return new SolidColorBrush(Color.FromRgb(gray, gray, gray));
    }

    internal record AnsiSegment(string Text, IBrush? Foreground);

    internal static List<AnsiSegment> ParseAnsi(string input)
    {
        var segments = new List<AnsiSegment>();
        IBrush? currentFg = null;
        int lastIndex = 0;

        foreach (Match match in AnsiRegex.Matches(input))
        {
            if (match.Index > lastIndex)
            {
                var text = input[lastIndex..match.Index];
                if (text.Length > 0)
                    segments.Add(new AnsiSegment(text, currentFg));
            }

            var codes = match.Groups[1].Value;
            currentFg = ParseSgrCodes(codes, currentFg);
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < input.Length)
            segments.Add(new AnsiSegment(input[lastIndex..], currentFg));

        return segments;
    }

    private static IBrush? ParseSgrCodes(string codes, IBrush? current)
    {
        if (string.IsNullOrEmpty(codes) || codes == "0")
            return null; // reset

        var parts = codes.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var code))
                continue;

            switch (code)
            {
                case 0:
                    current = null;
                    break;
                case >= 30 and <= 37:
                    current = Basic16[code - 30];
                    break;
                case >= 90 and <= 97:
                    current = Basic16[code - 90 + 8];
                    break;
                case 38 when i + 1 < parts.Length:
                    if (int.TryParse(parts[i + 1], out var mode))
                    {
                        if (mode == 5 && i + 2 < parts.Length && int.TryParse(parts[i + 2], out var colorIdx))
                        {
                            current = Color256ToBrush(colorIdx);
                            i += 2;
                        }
                        else if (mode == 2 && i + 4 < parts.Length
                            && int.TryParse(parts[i + 2], out var r)
                            && int.TryParse(parts[i + 3], out var g)
                            && int.TryParse(parts[i + 4], out var b))
                        {
                            current = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
                            i += 4;
                        }
                    }
                    break;
                case 39:
                    current = null;
                    break;
            }
        }
        return current;
    }
}
