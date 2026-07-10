using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// Pure unit tests on the SessionQueue store. TmuxClient is never touched —
/// flush-into-tmux verification is done via live scratch-project per the project's
/// no-mock-tmux convention.
/// </summary>
public class SessionQueueTests
{
    private static SessionQueue.QueuedReply Entry(string projectId, string text = "hi", long? tokenId = 1)
        => new(projectId, text, "hash-" + text, tokenId, "sess-uuid", DateTimeOffset.UtcNow);

    [Fact]
    public void PutOrReplace_NewSlot_ReturnsNull()
    {
        var q = new SessionQueue();
        var prior = q.PutOrReplace(Entry("proj-A"));
        Assert.Null(prior);
        Assert.NotNull(q.Peek("proj-A"));
    }

    [Fact]
    public void PutOrReplace_ExistingSlot_ReturnsPrior_AndReplaces()
    {
        var q = new SessionQueue();
        var first = Entry("proj-A", "first");
        var second = Entry("proj-A", "second");
        q.PutOrReplace(first);

        var prior = q.PutOrReplace(second);

        Assert.NotNull(prior);
        Assert.Equal("first", prior!.Text);
        Assert.Equal("second", q.Peek("proj-A")!.Text);
    }

    [Fact]
    public void TryDequeue_RemovesAndReturnsEntry()
    {
        var q = new SessionQueue();
        q.PutOrReplace(Entry("proj-A"));
        var popped = q.TryDequeue("proj-A");
        Assert.NotNull(popped);
        Assert.Null(q.Peek("proj-A"));
    }

    [Fact]
    public void TryDequeue_Empty_ReturnsNull()
    {
        var q = new SessionQueue();
        Assert.Null(q.TryDequeue("proj-A"));
    }

    [Fact]
    public void Clear_Idempotent()
    {
        var q = new SessionQueue();
        Assert.False(q.Clear("proj-A"));   // empty -> false
        q.PutOrReplace(Entry("proj-A"));
        Assert.True(q.Clear("proj-A"));    // present -> true
        Assert.False(q.Clear("proj-A"));   // gone -> false again
    }

    [Fact]
    public void SeparateProjects_AreIndependent()
    {
        var q = new SessionQueue();
        q.PutOrReplace(Entry("proj-A", "a-text"));
        q.PutOrReplace(Entry("proj-B", "b-text"));

        Assert.Equal("a-text", q.Peek("proj-A")!.Text);
        Assert.Equal("b-text", q.Peek("proj-B")!.Text);

        q.Clear("proj-A");
        Assert.Null(q.Peek("proj-A"));
        Assert.NotNull(q.Peek("proj-B"));
    }

    [Fact]
    public void Snapshot_ReturnsAllSlots()
    {
        var q = new SessionQueue();
        q.PutOrReplace(Entry("proj-A"));
        q.PutOrReplace(Entry("proj-B"));
        q.PutOrReplace(Entry("proj-C"));

        var snap = q.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.Contains(snap, e => e.ProjectId == "proj-A");
        Assert.Contains(snap, e => e.ProjectId == "proj-B");
        Assert.Contains(snap, e => e.ProjectId == "proj-C");
    }
}
