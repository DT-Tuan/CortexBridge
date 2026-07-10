using System.Text.Json.Serialization;

namespace CortexBridge.Api.Cortex;

/// <summary>
/// One cross-project memory row as CortexPlexus returns it (the inner JSON of a
/// list_memories / recall_memory tool result). Only the fields the cockpit shows
/// are mapped; unknown fields (scopeId, relatedFqns, …) are ignored.
/// </summary>
public record CortexMemory(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("repository")] string? Repository,
    [property: JsonPropertyName("topic")] string? Topic,
    [property: JsonPropertyName("importance")] double? Importance,
    [property: JsonPropertyName("score")] double? Score,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("lastAccessedAt")] string? LastAccessedAt,
    [property: JsonPropertyName("accessCount")] int? AccessCount
);

/// <summary>list_memories / recall_memory payload shape: {count, memories[]}.</summary>
public record CortexMemoryList(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("memories")] List<CortexMemory> Memories
);

/// <summary>save_memory result shape: {id, scope, topic, importance, savedAt, stored}.</summary>
public record CortexSaveResult(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("topic")] string? Topic,
    [property: JsonPropertyName("importance")] double? Importance,
    [property: JsonPropertyName("savedAt")] string? SavedAt,
    [property: JsonPropertyName("stored")] bool Stored
);

/// <summary>
/// Raised on any CortexPlexus transport/timeout/shape failure. The endpoint maps
/// it to a typed 503 so the cockpit degrades to "không khả dụng" and the bridge
/// itself stays healthy (CortexPlexus is never on a hot path).
/// </summary>
public sealed class CortexPlexusUnavailableException : Exception
{
    public CortexPlexusUnavailableException(string message) : base(message) { }
    public CortexPlexusUnavailableException(string message, Exception inner) : base(message, inner) { }
}
