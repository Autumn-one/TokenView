namespace TokenView.Wpf.Data.Usage;

/// <summary>Qoder CLI 占位 usage 的估算规则（E5）；结果必须按 estimated 单独展示。</summary>
public static class QoderCliEstimate
{
    public const long ContextWindowTokens = 200_000;
    public const int CharsPerToken = 4;
    public const double Confidence = 0.8;

    public static RawTokenCounts? Compute(double? contextUsageRatio, long outputChars)
    {
        if (contextUsageRatio is not { } ratio || !double.IsFinite(ratio) || ratio <= 0 || ratio > 1)
        {
            return null;
        }
        var input = (long)Math.Round(ratio * ContextWindowTokens, MidpointRounding.AwayFromZero);
        var output = outputChars <= 0 ? 0 : (outputChars + CharsPerToken - 1) / CharsPerToken;
        if (input <= 0 || input > long.MaxValue - output)
        {
            return null;
        }
        // 缓存桶置 0 只用于让总量可求和，并不代表客户端报告了实测缓存。
        return new RawTokenCounts(input, output, 0, 0);
    }
}
