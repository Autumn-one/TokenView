using System.Globalization;
using TokenView.Wpf.Settings;

namespace TokenView.Wpf.Data;

/// <summary>Token 展示口径；仅影响显示，数据库始终保存原始计数。</summary>
public static class TokenFormatting
{
    public static TokenDisplay Display { get; set; } = TokenDisplay.Auto;

    public static string Format(long value)
    {
        if (Display == TokenDisplay.Exact)
        {
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }
        if (Display == TokenDisplay.Auto)
        {
            return Compact(value);
        }
        var absolute = Math.Abs(value);
        if (Display == TokenDisplay.Chinese && absolute < 10_000)
        {
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }
        var (divisor, suffix) = Display switch
        {
            TokenDisplay.Chinese when absolute >= 100_000_000 => (100_000_000d, "亿"),
            TokenDisplay.Chinese => (10_000d, "万"),
            TokenDisplay.K => (1_000d, "k"),
            TokenDisplay.M => (1_000_000d, "M"),
            _ => (1_000_000_000d, "B"),
        };
        if (value == 0) return $"0 {suffix}";
        if (absolute / divisor < 0.01) return $"{(value < 0 ? "−" : "")}<0.01 {suffix}";
        return $"{value / divisor:0.00} {suffix}";
    }

    private static string Compact(long value)
    {
        var absolute = Math.Abs(value);
        if (absolute >= 1_000_000_000) return $"{value / 1_000_000_000d:0.00}B";
        if (absolute >= 1_000_000) return $"{value / 1_000_000d:0.00}M";
        if (absolute >= 1_000) return $"{value / 1_000d:0.0}k";
        return value.ToString("N0", CultureInfo.CurrentCulture);
    }
}
