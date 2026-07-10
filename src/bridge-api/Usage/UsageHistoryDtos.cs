namespace CortexBridge.Api.Usage;

public record UsageHistoryResponse(UsageHistoryPoint[] Points);

public record UsageHistoryPoint(
    string TakenUtc,
    decimal Block5hCurrentUsd,
    decimal Block5hProjectedUsd,
    decimal Block5hPctCurrent,
    decimal Block5hPctProjected,
    decimal Week7dCurrentUsd,
    decimal Week7dPctCurrent);
