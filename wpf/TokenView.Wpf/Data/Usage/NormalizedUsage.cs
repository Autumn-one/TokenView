namespace TokenView.Wpf.Data.Usage;

/// <summary>客户端原始计数；null 表示字段缺失，和 0 语义不同。</summary>
public readonly record struct RawTokenCounts(
    long? Input = null,
    long? Output = null,
    long? CacheRead = null,
    long? CacheWrite = null,
    long? Reasoning = null,
    long? ClientTotal = null)
{
    public static RawTokenCounts Sum(IEnumerable<RawTokenCounts> items)
    {
        long? input = null, output = null, cacheRead = null, cacheWrite = null, reasoning = null, total = null;
        foreach (var item in items)
        {
            Add(ref input, item.Input);
            Add(ref output, item.Output);
            Add(ref cacheRead, item.CacheRead);
            Add(ref cacheWrite, item.CacheWrite);
            Add(ref reasoning, item.Reasoning);
            Add(ref total, item.ClientTotal);
        }
        return new RawTokenCounts(input, output, cacheRead, cacheWrite, reasoning, total);
    }

    private static void Add(ref long? slot, long? value)
    {
        if (value is { } number)
        {
            var current = slot ?? 0;
            slot = current > long.MaxValue - number ? long.MaxValue : current + number;
        }
    }
}

public readonly record struct TokenBuckets(
    long? Input = null,
    long? Output = null,
    long? Reasoning = null,
    long? CacheRead = null,
    long? CacheWrite = null)
{
    public long? SumWith(TokenSemantics semantics)
    {
        long total = 0;
        foreach (var (value, required) in new[]
                 {
                     (Input, true),
                     (Output, true),
                     (Reasoning, semantics.Reasoning != FieldRelation.NotProvided),
                     (CacheRead, true),
                     (CacheWrite, semantics.CacheWriteCountsInTotal),
                 })
        {
            if (!required) continue;
            if (value is not { } number || total > long.MaxValue - number) return null;
            total += number;
        }
        return total;
    }
}

public enum UsageTotalBasis
{
    ClientReported,
    DerivedFromBuckets,
    Unavailable,
}

public sealed record NormalizedUsage(
    TokenBuckets Buckets,
    long? TotalTokens,
    UsageTotalBasis TotalBasis,
    IReadOnlyList<string> UnknownRelations,
    bool ClientTotalMismatch);

public static class UsageNormalizer
{
    public static NormalizedUsage Normalize(TokenSemantics semantics, RawTokenCounts raw)
    {
        var unknown = new List<string>();

        long? inputBucket;
        long? cacheReadBucket;
        if (raw.Input is { } input && raw.CacheRead is { } cacheRead)
        {
            if (semantics.CacheRead == FieldRelation.IncludedIn)
            {
                if (cacheRead > input)
                {
                    unknown.Add(TokenSemanticsCatalog.AnomalyCacheReadExceedsInput);
                    inputBucket = 0;
                    cacheReadBucket = cacheRead;
                }
                else
                {
                    inputBucket = input - cacheRead;
                    cacheReadBucket = cacheRead;
                }
            }
            else
            {
                inputBucket = input;
                cacheReadBucket = cacheRead;
            }
        }
        else if (raw.Input is { } onlyInput)
        {
            if (semantics.CacheRead == FieldRelation.IncludedIn)
            {
                unknown.Add(TokenSemanticsCatalog.UnknownCacheReadMissing);
            }
            inputBucket = onlyInput;
            cacheReadBucket = null;
        }
        else
        {
            inputBucket = null;
            cacheReadBucket = raw.CacheRead;
        }

        long? outputBucket;
        long? reasoningBucket;
        if (raw.Output is { } output && raw.Reasoning is { } reasoning)
        {
            if (semantics.Reasoning == FieldRelation.IncludedIn)
            {
                if (reasoning > output)
                {
                    unknown.Add(TokenSemanticsCatalog.AnomalyReasoningExceedsOutput);
                    outputBucket = 0;
                    reasoningBucket = reasoning;
                }
                else
                {
                    outputBucket = output - reasoning;
                    reasoningBucket = reasoning;
                }
            }
            else
            {
                outputBucket = output;
                reasoningBucket = reasoning;
            }
        }
        else if (raw.Output is { } onlyOutput)
        {
            if (semantics.Reasoning == FieldRelation.IncludedIn)
            {
                unknown.Add(TokenSemanticsCatalog.UnknownReasoningMissing);
            }
            outputBucket = onlyOutput;
            reasoningBucket = null;
        }
        else
        {
            outputBucket = null;
            reasoningBucket = raw.Reasoning;
        }

        if (semantics.CacheWriteInInput == FieldRelation.Unknown && raw.CacheWrite is not null)
        {
            unknown.Add(TokenSemanticsCatalog.UnknownCacheWriteRelation);
        }

        var buckets = new TokenBuckets(inputBucket, outputBucket, reasoningBucket, cacheReadBucket, raw.CacheWrite);
        var bucketSum = buckets.SumWith(semantics);

        long? totalTokens;
        UsageTotalBasis totalBasis;
        var mismatch = false;
        if (raw.ClientTotal is { } clientTotal)
        {
            totalTokens = clientTotal;
            totalBasis = UsageTotalBasis.ClientReported;
            if (bucketSum is { } sum)
            {
                mismatch = clientTotal != sum;
            }
        }
        else if (bucketSum is { } sum)
        {
            totalTokens = sum;
            totalBasis = UsageTotalBasis.DerivedFromBuckets;
        }
        else
        {
            totalTokens = null;
            totalBasis = UsageTotalBasis.Unavailable;
        }

        if (totalBasis == UsageTotalBasis.Unavailable)
        {
            unknown.Add(TokenSemanticsCatalog.UnknownTotalIncomplete);
        }

        unknown.Sort(StringComparer.Ordinal);
        return new NormalizedUsage(buckets, totalTokens, totalBasis,
            unknown.Distinct(StringComparer.Ordinal).ToList(), mismatch);
    }
}
