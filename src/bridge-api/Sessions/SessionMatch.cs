namespace CortexBridge.Api.Sessions;

/// <summary>
/// ADR-016 Slice 2 Step 0 — pure decision: does the caller's <c>?session=</c>
/// match the project's current live-slot UID? Used by every write endpoint
/// (reply / choice / quick-reply / cancel-picker) before it acquires the reply
/// mutex, so a mismatch never contends with a parallel real reply.
///
/// No DB, no I/O, no time — the same low-risk keystone pattern used in
/// Slice 1's <see cref="SessionOwnershipRegistry.PickLiveSession"/>.
/// </summary>
public static class SessionMatch
{
    public enum Result { Ok, Mismatch }

    /// <summary>
    /// <list type="bullet">
    ///   <item><c>requested</c> null/empty ⇒ <see cref="Result.Ok"/>
    ///         — backward-compat: clients that omit <c>?session=</c> get the
    ///         project's live-slot UID (Slice 1 behaviour).</item>
    ///   <item><c>requested</c> set, <c>active</c> null/empty
    ///         ⇒ <see cref="Result.Mismatch"/> (no live slot to match).</item>
    ///   <item>Both set, equal (case-insensitive) ⇒ <see cref="Result.Ok"/>.</item>
    ///   <item>Both set, unequal ⇒ <see cref="Result.Mismatch"/>.</item>
    /// </list>
    /// </summary>
    public static Result Check(string? requested, string? active)
    {
        if (string.IsNullOrEmpty(requested)) return Result.Ok;
        if (string.IsNullOrEmpty(active)) return Result.Mismatch;
        return string.Equals(requested, active, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok
            : Result.Mismatch;
    }

    // Same regex `resume` validates with — applied at write-endpoint entry so a
    // bad `?session=` is rejected before any DB/tmux lookup.
    private static readonly System.Text.RegularExpressions.Regex UuidShape =
        new("^[A-Za-z0-9._-]{1,128}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsValidUuidShape(string s) => UuidShape.IsMatch(s);
}
