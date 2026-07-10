using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// IdeLockReader.ParseProjects is the auto-mode B-presence signal (ADR-017):
/// lock JSON → projectIds. Must be tolerant (garbled lock ⇒ nothing, never
/// throws) so a mid-write lockfile can't crash the ModeWatcher.
/// </summary>
public class IdeLockReaderTests
{
    [Fact]
    public void RealLock_YieldsWorkspaceBasename()
    {
        const string json =
            """{"pid":991882,"workspaceFolders":["/home/youruser/workspace/CortexBridge"],"ideName":"Visual Studio Code","transport":"ws"}""";
        Assert.Equal(new[] { "CortexBridge" }, IdeLockReader.ParseProjects(json));
    }

    [Fact]
    public void MultipleWorkspaceFolders_AllYielded_TrailingSlashOk()
    {
        const string json =
            """{"workspaceFolders":["/home/youruser/workspace/CortexBridge/","/srv/project-zeta"]}""";
        Assert.Equal(new[] { "CortexBridge", "project-zeta" }, IdeLockReader.ParseProjects(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not json {{{")]
    [InlineData("""{"pid":1}""")]                          // no workspaceFolders
    [InlineData("""{"workspaceFolders":"not-an-array"}""")] // wrong shape
    [InlineData("""{"workspaceFolders":[]}""")]             // empty
    [InlineData("""{"workspaceFolders":[123,null]}""")]     // non-string entries
    public void GarbledOrEmpty_YieldsNothing_NeverThrows(string? json)
        => Assert.Empty(IdeLockReader.ParseProjects(json));
}
