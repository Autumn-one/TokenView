using TokenView.Wpf.Data.Usage;

namespace TokenView.Wpf.Data.Collectors;

public sealed record RequestMeta(
    string Key,
    string? Model,
    string? Agent,
    long? CreatedMs,
    long? CompletedMs,
    bool Sidechain,
    string SourceRef);

public sealed record SessionMeta(
    string Id,
    string? Parent,
    string? Project,
    long? StartMs,
    long? EndMs);

/// <summary>请求级采集器共用的归一化入口。</summary>
public static class RequestBuilder
{
    public static MessageRecord Request(string client, string sessionId, RequestMeta meta, RawTokenCounts raw)
    {
        var semantics = TokenSemanticsCatalog.For(client);
        return new MessageRecord
        {
            Client = client,
            ClientSessionId = sessionId,
            DedupKey = meta.Key,
            Seq = null,
            CreatedAtMs = meta.CreatedMs,
            CompletedAtMs = meta.CompletedMs,
            Model = meta.Model,
            Agent = meta.Agent,
            Usage = UsageNormalizer.Normalize(semantics, raw),
            IsSidechain = meta.Sidechain,
            SemanticsId = semantics.Id,
            SourceRef = meta.SourceRef,
        };
    }

    public static MessageRecord MessageWithSemantics(string client, TokenSemantics semantics, string sessionId,
        RequestMeta meta, RawTokenCounts raw)
    {
        return new MessageRecord
        {
            Client = client,
            ClientSessionId = sessionId,
            DedupKey = meta.Key,
            Seq = null,
            CreatedAtMs = meta.CreatedMs,
            CompletedAtMs = meta.CompletedMs,
            Model = meta.Model,
            Agent = meta.Agent,
            Usage = UsageNormalizer.Normalize(semantics, raw),
            IsSidechain = meta.Sidechain,
            SemanticsId = semantics.Id,
            SourceRef = meta.SourceRef,
        };
    }

    public static SessionRecord Session(string client, SessionMeta meta, IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<RawTokenCounts> counts, bool partial)
    {
        var semantics = TokenSemanticsCatalog.For(client);
        return SessionWithSemantics(client, semantics, meta, messages, counts, partial);
    }

    public static SessionRecord SessionWithSemantics(string client, TokenSemantics semantics, SessionMeta meta,
        IReadOnlyList<MessageRecord> messages, IReadOnlyList<RawTokenCounts> counts, bool partial)
    {
        var raw = SumCompleteCounts(counts);

        long? summedTotal = null;
        if (messages.Count > 0)
        {
            long sum = 0;
            var complete = true;
            foreach (var message in messages)
            {
                if (message.Usage.TotalTokens is not { } value || sum > long.MaxValue - value)
                {
                    complete = false;
                    break;
                }
                sum += value;
            }
            summedTotal = complete ? sum : null;
        }
        raw = raw with { ClientTotal = summedTotal };

        var normalized = UsageNormalizer.Normalize(semantics, raw);
        var unknown = normalized.UnknownRelations.ToList();
        UsageTotalBasis basis;
        if (summedTotal is not null)
        {
            basis = UsageTotalBasis.DerivedFromBuckets;
        }
        else
        {
            if (!unknown.Contains(TokenSemanticsCatalog.UnknownTotalIncomplete))
            {
                unknown.Add(TokenSemanticsCatalog.UnknownTotalIncomplete);
            }
            basis = UsageTotalBasis.Unavailable;
        }
        unknown.Sort(StringComparer.Ordinal);

        var usage = normalized with
        {
            TotalTokens = summedTotal,
            TotalBasis = basis,
            UnknownRelations = unknown,
            ClientTotalMismatch = normalized.ClientTotalMismatch ||
                                  messages.Any(message => message.Usage.ClientTotalMismatch),
        };

        MessageRecord? modelMessage = null;
        foreach (var message in messages)
        {
            if (message.Model is null) continue;
            if (modelMessage is null ||
                (message.CreatedAtMs ?? long.MinValue) >= (modelMessage.CreatedAtMs ?? long.MinValue))
            {
                modelMessage = message;
            }
        }

        long? ended = meta.EndMs;
        foreach (var message in messages)
        {
            var value = message.CompletedAtMs ?? message.CreatedAtMs;
            if (value is { } candidate && (ended is null || candidate > ended)) ended = candidate;
        }

        var coverage = partial
            ? MessageCoverage.Partial
            : messages.Count == 0
                ? MessageCoverage.NoMessages
                : MessageCoverage.Complete;

        return new SessionRecord
        {
            Client = client,
            ClientSessionId = meta.Id,
            ParentClientSessionId = meta.Parent,
            ProjectPath = meta.Project,
            Model = modelMessage?.Model,
            StartedAtMs = meta.StartMs,
            EndedAtMs = ended,
            SemanticsId = semantics.Id,
            TotalBasis = SessionTotalBasis.SumOfMessages,
            Coverage = coverage,
            Usage = usage,
        };
    }

    /// <summary>缺失字段在任意一条请求缺失时保持未知；溢出时整组归零。</summary>
    public static RawTokenCounts SumCompleteCounts(IReadOnlyList<RawTokenCounts> items)
    {
        static long? Sum(IReadOnlyList<RawTokenCounts> items, Func<RawTokenCounts, long?> selector)
        {
            if (items.Count == 0) return null;
            long total = 0;
            foreach (var item in items)
            {
                if (selector(item) is not { } value || total > long.MaxValue - value) return null;
                total += value;
            }
            return total;
        }

        var result = new RawTokenCounts(
            Sum(items, raw => raw.Input),
            Sum(items, raw => raw.Output),
            Sum(items, raw => raw.CacheRead),
            Sum(items, raw => raw.CacheWrite),
            Sum(items, raw => raw.Reasoning),
            Sum(items, raw => raw.ClientTotal));

        long combined = 0;
        foreach (var value in new[] { result.Input, result.Output, result.CacheRead, result.CacheWrite, result.Reasoning })
        {
            if (value is not { } number) continue;
            if (combined > long.MaxValue - number) return default;
            combined += number;
        }
        return result;
    }
}
