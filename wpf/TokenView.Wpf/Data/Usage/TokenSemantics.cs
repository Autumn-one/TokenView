namespace TokenView.Wpf.Data.Usage;

public enum FieldRelation
{
    IncludedIn,
    Separate,
    NotProvided,
    Unknown,
}

public sealed record TokenSemantics(
    string Id,
    FieldRelation CacheRead,
    FieldRelation Reasoning,
    FieldRelation CacheWriteInInput,
    bool CacheWriteCountsInTotal,
    bool ClientReportsTotal,
    string Basis);

public static class TokenSemanticsCatalog
{
    public const string UnknownCacheWriteRelation = "cache_write.relation_to_input";
    public const string UnknownReasoningMissing = "reasoning.missing";
    public const string UnknownCacheReadMissing = "cache_read.missing";
    public const string UnknownTotalIncomplete = "total.unavailable_due_to_missing_fields";
    public const string AnomalyCacheReadExceedsInput = "anomaly.cache_read_gt_input";
    public const string AnomalyReasoningExceedsOutput = "anomaly.reasoning_gt_output";

    public static readonly TokenSemantics Codex = new(
        "codex.usage.v1", FieldRelation.IncludedIn, FieldRelation.IncludedIn,
        FieldRelation.Unknown, false, true,
        "local measurement: total == input + output for all 19385 records");

    public static readonly TokenSemantics ClaudeCode = new(
        "claude_code.anthropic_usage.v1", FieldRelation.Separate, FieldRelation.NotProvided,
        FieldRelation.Separate, true, false,
        "local measurement: cache_read > input on 3927/4956 records");

    public static readonly TokenSemantics OpenCode = new(
        "opencode.message.v1", FieldRelation.Separate, FieldRelation.Separate,
        FieldRelation.Unknown, false, true,
        "local measurement: total == input + output + reasoning + cache.read");

    public static readonly TokenSemantics Dsh = new(
        "dsh.token_usage.v1", FieldRelation.Separate, FieldRelation.IncludedIn,
        FieldRelation.Separate, true, false,
        "DSH token-meter contract: input/cache read/cache write disjoint; reasoning included in output");

    public static readonly TokenSemantics KimiCode = new(
        "kimi_code.usage_record.v1", FieldRelation.Separate, FieldRelation.NotProvided,
        FieldRelation.Separate, true, false,
        "Kimi provider/addUsage contract: inputOther + output + inputCacheRead + inputCacheCreation");

    public static readonly TokenSemantics MiMoCode = new(
        "mimocode.message.v1", FieldRelation.Separate, FieldRelation.Separate,
        FieldRelation.Separate, true, false,
        "installed MiMo getUsage contract: input/output/reasoning/cache are disjoint buckets");

    public static readonly TokenSemantics PiDesktop = new(
        "pi_desktop.operation.v1", FieldRelation.Separate, FieldRelation.IncludedIn,
        FieldRelation.Separate, true, true,
        "Pi Desktop request accounting contract and local total == input + output + cacheRead + cacheWrite");

    public static readonly TokenSemantics PiAgent = new(
        "pi_agent.session.v3", FieldRelation.Separate, FieldRelation.IncludedIn,
        FieldRelation.Separate, true, true,
        "Pi coding agent 1.0.4 session usage: totalTokens == input + output + cacheRead + cacheWrite");

    public static readonly TokenSemantics WorkBuddy = new(
        "workbuddy.item_usage.v1", FieldRelation.IncludedIn, FieldRelation.IncludedIn,
        FieldRelation.Unknown, false, true,
        "WorkBuddy 5.2.6 last response item contract; total == input + output; cached_tokens is an input detail");

    public static readonly TokenSemantics Copilot = new(
        "copilot.assistant_usage.v1", FieldRelation.Separate, FieldRelation.IncludedIn,
        FieldRelation.Separate, true, false,
        "Copilot CLI 1.0.91 FJt accounting: native input includes read/write caches; reasoning is an output detail");

    public static readonly TokenSemantics GeminiCli = new(
        "gemini_cli.response.v1", FieldRelation.IncludedIn, FieldRelation.Separate,
        FieldRelation.NotProvided, false, true,
        "Gemini CLI recorder/metrics contract: prompt includes cached; thoughts are separate output");

    public static readonly TokenSemantics Grok = new(
        "grok.turn.v1", FieldRelation.IncludedIn, FieldRelation.IncludedIn,
        FieldRelation.Unknown, false, true,
        "Grok usage display contract: input includes cached/output includes reasoning");

    public static readonly TokenSemantics Mcode = new(
        "mcode.request.v1", FieldRelation.Separate, FieldRelation.Unknown,
        FieldRelation.Separate, true, false,
        "MCode local-runtime usage API sums four disjoint buckets");

    public static readonly TokenSemantics Devin = new(
        "devin.request.v1", FieldRelation.Separate, FieldRelation.NotProvided,
        FieldRelation.Separate, true, false,
        "Devin metrics are Anthropic-shaped: num_tokens_preceding == input + cache_read + cache_creation");

    public static readonly TokenSemantics CommandCode = new(
        "command_code.ai_sdk_usage.v1", FieldRelation.Separate, FieldRelation.NotProvided,
        FieldRelation.Separate, true, false,
        "Command Code AI SDK totalUsage: inputTokens includes cache details; reasoning not reported");

    public static readonly TokenSemantics Cline = new(
        "cline.agent_events.v1", FieldRelation.Separate, FieldRelation.NotProvided,
        FieldRelation.Separate, true, false,
        "Cline agent-events contract: tokensIn is uncached input; cache read/write are disjoint");

    public static readonly TokenSemantics QoderIde = new(
        "qoder.ide_chat_message.v1", FieldRelation.IncludedIn, FieldRelation.NotProvided,
        FieldRelation.NotProvided, false, false,
        "Qoder IDE token_info: prompt_tokens includes cached_tokens");

    public static readonly TokenSemantics QoderCli = new(
        "qoder.cli_message.v1", FieldRelation.Separate, FieldRelation.NotProvided,
        FieldRelation.Separate, true, false,
        "Qoder CLI message.usage is Anthropic-shaped");

    public static readonly TokenSemantics Qoder = new(
        "qoder.usage.v1", FieldRelation.Separate, FieldRelation.NotProvided,
        FieldRelation.Separate, true, false,
        "Qoder IDE token_info and CLI Anthropic usage");

    public static readonly TokenSemantics CodeArts = new(
        "codearts.usage.v1", FieldRelation.IncludedIn, FieldRelation.IncludedIn,
        FieldRelation.NotProvided, false, true,
        "CodeArts kernel messages.index token_usage: prompt includes cached; completion includes reasoning; total = prompt + completion");

    public static TokenSemantics For(string client) => client switch
    {
        "codex" => Codex,
        "claude_code" => ClaudeCode,
        "opencode" => OpenCode,
        "dsh" => Dsh,
        "kimi_code" => KimiCode,
        "mimocode" => MiMoCode,
        "pi_desktop" => PiDesktop,
        "pi_agent" => PiAgent,
        "workbuddy" => WorkBuddy,
        "copilot" => Copilot,
        "gemini_cli" => GeminiCli,
        "grok" => Grok,
        "mcode" => Mcode,
        "devin" => Devin,
        "command_code" => CommandCode,
        "cline" => Cline,
        "qoder" => Qoder,
        "codearts" => CodeArts,
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, "Unknown client"),
    };
}
