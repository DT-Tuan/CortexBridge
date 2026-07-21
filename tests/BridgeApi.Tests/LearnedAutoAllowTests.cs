using CortexBridge.Api.Endpoints;

namespace CortexBridge.Api.Tests;

/// <summary>
/// Guards LearnedAutoAllow. ADR-028 D: exact-string Bash learning is RETIRED (it never
/// re-matched and captured plaintext secrets); only tool-NAME learning survives.
/// </summary>
public class LearnedAutoAllowTests : IDisposable
{
    private readonly string _claudeDir;
    private readonly string _flagDir;
    private const string Proj = "proj";

    public LearnedAutoAllowTests()
    {
        _claudeDir = Path.Combine(Path.GetTempPath(), "cb-learned-" + Guid.NewGuid().ToString("N"));
        Assert.True(AutoAllowFlags.TryFlagDir(_claudeDir, Proj, out _flagDir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_claudeDir)) Directory.Delete(_claudeDir, recursive: true);
    }

    [Fact]
    public void Append_BashCommand_IsRetired_NotStored()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "Bash", "git status");
        LearnedAutoAllow.Append(_flagDir, Proj, "Bash", "ssh host 'grep TOKEN .env'");

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Empty(data.BashCommands);   // Bash learning retired — nothing captured (no secret surface)
        Assert.Empty(data.Tools);
    }

    [Fact]
    public void Append_NonBashTool_AppearsInTools()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "Read", "");

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Contains("Read", data.Tools);
        Assert.Empty(data.BashCommands);
    }

    [Fact]
    public void Append_SameTool_Twice_StoredOnce()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "Grep", "");
        LearnedAutoAllow.Append(_flagDir, Proj, "Grep", "");

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Single(data.Tools);
    }

    [Fact]
    public void Append_EmptyTool_NotStored()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "", "some command");

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Empty(data.Tools);
        Assert.Empty(data.BashCommands);
    }

    [Fact]
    public void NeverLearnTools_NotStored()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "AskUserQuestion", "");
        LearnedAutoAllow.Append(_flagDir, Proj, "ExitPlanMode", "");

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Empty(data.Tools);
    }

    [Fact]
    public void ReadData_NoFile_ReturnsEmpty()
    {
        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Empty(data.BashCommands);
        Assert.Empty(data.Tools);
    }

    [Fact]
    public void Append_CorruptFile_DoesNotThrow_StartsClean()
    {
        Directory.CreateDirectory(_flagDir);
        File.WriteAllText(Path.Combine(_flagDir, Proj + ".learned.json"), "not valid json {{{{");

        var ex = Record.Exception(() => LearnedAutoAllow.Append(_flagDir, Proj, "Write", ""));
        Assert.Null(ex);

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Contains("Write", data.Tools);
    }

    [Fact]
    public void JsonFile_CamelCase_Keys()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "Write", "");

        var raw = File.ReadAllText(Path.Combine(_flagDir, Proj + ".learned.json"));
        Assert.Contains("bashCommands", raw);
        Assert.Contains("tools", raw);
        Assert.DoesNotContain("BashCommands", raw);
    }
}
