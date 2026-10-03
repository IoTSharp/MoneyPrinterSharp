using System.Globalization;
using System.Text;

namespace VideoProduction;

/// <summary>统一中英文 Unicode 文本、换行和字体回退，供字幕与界面共用。</summary>
public static class MpsTextLayout
{
    private static readonly string[] DefaultFallbackFonts = ["Microsoft YaHei UI", "Segoe UI", "Arial", "sans-serif"];

    /// <summary>按 NFC 规范化文本，统一换行并去除不可见控制字符（保留制表和换行）。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n');
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\t' || !Rune.IsControl(rune)) builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>按 Unicode 文本元素换行，中文逐元素断行，英文优先按空格断行。</summary>
    public static IReadOnlyList<string> Wrap(string? text, int maxUnits)
    {
        if (maxUnits is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxUnits));
        var value = Normalize(text);
        var lines = new List<string>();
        foreach (var paragraph in value.Split('\n'))
        {
            if (paragraph.Length == 0) { lines.Add(string.Empty); continue; }
            var current = new StringBuilder();
            var lastBreak = -1;
            var units = 0;
            var starts = StringInfo.ParseCombiningCharacters(paragraph);
            for (var index = 0; index < starts.Length; index++)
            {
                var element = starts[index];
                var next = index + 1 < starts.Length ? starts[index + 1] : paragraph.Length;
                var grapheme = paragraph[element..next];
                var width = IsWide(grapheme) ? 2 : 1;
                if (units + width > maxUnits && current.Length > 0)
                {
                    if (lastBreak >= 0)
                    {
                        lines.Add(current.ToString(0, lastBreak).TrimEnd());
                        var remainder = current.ToString(lastBreak + 1, current.Length - lastBreak - 1).TrimStart();
                        current.Clear().Append(remainder);
                        units = Measure(remainder);
                    }
                    else
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        units = 0;
                    }
                    lastBreak = -1;
                }
                current.Append(grapheme);
                units += width;
                if (grapheme is " " or "\t") lastBreak = current.Length - 1;
            }
            if (current.Length > 0) lines.Add(current.ToString().TrimEnd());
        }
        return lines;
    }

    /// <summary>从用户候选字体中选出可用回退链；未安装字体保留为声明而不伪造可用性。</summary>
    public static IReadOnlyList<string> ResolveFallbackFonts(IEnumerable<string>? candidates = null)
    {
        var values = (candidates ?? DefaultFallbackFonts).Where(font => !string.IsNullOrWhiteSpace(font))
            .Select(font => font.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        return values.Length == 0 ? DefaultFallbackFonts : values;
    }

    private static int Measure(string value) => value.EnumerateRunes().Sum(rune => IsWide(rune.ToString()) ? 2 : 1);
    private static bool IsWide(string value) => value.EnumerateRunes().Any(rune => rune.Value > 0x2E7F || rune.Value is >= 0x1100 and <= 0x11FF);
}
