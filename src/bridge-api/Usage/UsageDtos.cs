namespace CortexBridge.Api.Usage;

public record UsageResponse(
    string TakenAtUtc,
    Block5hDto? Block5h,
    Week7dDto? Week7d,
    ProjectUsageDto[] Projects,
    OfficialUsageDto? Official);

/// <summary>
/// Official Anthropic plan quota (ADR-024) — the exact numbers Claude Code's
/// /usage panel shows, sampled host-side from the OAuth usage endpoint. This
/// is the SOLE source for the 5h/7d gauges; ccusage-derived cost below is
/// "where did my budget go" detail, never a quota %.
/// TakenAtUtc is the official sample's own timestamp — when the endpoint is
/// unreachable the sampler carries the last-known block forward unchanged, so
/// staleness is detectable here (PWA renders a stale-badge).
/// </summary>
public record OfficialUsageDto(
    OfficialWindowDto? FiveHour,
    OfficialWindowDto? SevenDay,
    string TakenAtUtc);

public record OfficialWindowDto(
    decimal Utilization,
    string ResetsAt);

public record Block5hDto(
    string StartUtc,
    string EndUtc,
    int RemainingMinutes,
    decimal CurrentCostUsd,
    decimal ProjectedCostUsd,
    long CurrentTokens,
    long ProjectedTokens,
    decimal CostPerHour,
    decimal TokensPerMinute,
    string[] Models,
    int Entries);

public record Week7dDto(
    string PeriodStart,
    decimal CurrentCostUsd,
    long CurrentTokens,
    ModelUsageDto[] ModelBreakdown);

public record ModelUsageDto(
    string Model,
    decimal CostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens);

/// <summary>
/// Lifetime totals per project (cwd folder under ~/.claude/projects/),
/// sourced from <c>ccusage claude session --json</c> and aggregated by the
/// host sampler. No window cap — Anthropic quotas are account-wide, so this
/// view is "where did my budget go", not per-project rate-limiting.
/// </summary>
public record ProjectUsageDto(
    string Name,
    string EncodedPath,
    decimal TotalCostUsd,
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    int SessionCount,
    string? LastActivity,
    string[] Models);
