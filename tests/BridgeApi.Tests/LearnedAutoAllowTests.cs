using CortexBridge.Api.Endpoints;

namespace CortexBridge.Api.Tests;

/// <summary>
/// Guards LearnedAutoAllow: per-project command-learning store written by the
/// bridge (POST /autoallow/learn) and read by the host PreToolUse hook.
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
    public void Append_BashCommand_AppearsInBashCommands()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "Bash", "git status");

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Contains("git status", data.BashCommands);
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
    public void Append_SameBashCommand_Twice_StoredOnce()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "Bash", "git diff");
        LearnedAutoAllow.Append(_flagDir, Proj, "Bash", "git diff");

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Single(data.BashCommands);
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
    public void Append_MultipleBashCommands_AllStored()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "Bash", "ls -la");
        LearnedAutoAllow.Append(_flagDir, Proj, "Bash", "cat README.md");

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Contains("ls -la", data.BashCommands);
        Assert.Contains("cat README.md", data.BashCommands);
        Assert.Equal(2, data.BashCommands.Count);
    }

    [Fact]
    public void Append_EmptyBashCommand_NotStored()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "Bash", "");

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Empty(data.BashCommands);
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

        var ex = Record.Exception(() => LearnedAutoAllow.Append(_flagDir, Proj, "Bash", "git log"));
        Assert.Null(ex);

        var data = LearnedAutoAllow.ReadData(_flagDir, Proj);
        Assert.Contains("git log", data.BashCommands);
    }

    [Fact]
    public void JsonFile_CamelCase_BashCommandsKey()
    {
        LearnedAutoAllow.Append(_flagDir, Proj, "Bash", "pwd");

        var raw = File.ReadAllText(Path.Combine(_flagDir, Proj + ".learned.json"));
        Assert.Contains("bashCommands", raw);
        Assert.Contains("tools", raw);
        Assert.DoesNotContain("BashCommands", raw);
    }
}
