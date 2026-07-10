using CortexBridge.Api.Endpoints;

namespace CortexBridge.Api.Tests;

/// <summary>
/// FilesEndpoint backs the PWA composer "@" file-mention picker. Rank is the
/// pure scorer (filename match > path match, non-matches dropped, limit
/// honored); WalkRelativeFiles prunes build/dep dirs + returns forward-slash
/// relative paths.
/// </summary>
public class FilesEndpointTests
{
    [Fact]
    public void Rank_FilenameStartsWith_BeatsPathContains()
    {
        var files = new List<string>
        {
            "config/app.ts",   // filename "app.ts" — only the PATH contains "config" (score 2)
            "config.json",     // filename starts with "config" (score 0)
            "myconfig.ts"      // filename contains but doesn't start with "config" (score 1)
        };
        var r = FilesEndpoint.Rank(files, "config", 10);
        Assert.Equal("config.json", r[0]);     // filename starts-with wins
        Assert.Equal("myconfig.ts", r[1]);     // filename-contains next
        Assert.Equal("config/app.ts", r[2]);   // path-contains last
    }

    [Fact]
    public void Rank_DropsNonMatches_AndHonorsLimit()
    {
        var files = new List<string> { "a.ts", "b.ts", "match-one.ts", "match-two.ts", "c.ts" };
        var r = FilesEndpoint.Rank(files, "match", 1);
        Assert.Single(r);
        Assert.StartsWith("match-", r[0]);
    }

    [Fact]
    public void Rank_CaseInsensitive()
    {
        var files = new List<string> { "src/Foo/ReadMe.MD" };
        Assert.Single(FilesEndpoint.Rank(files, "readme", 10));
    }

    [Fact]
    public void Rank_EmptyQuery_ReturnsShallowFirst_UpToLimit()
    {
        var files = new List<string> { "a/b/c/deep.ts", "top.ts", "a/mid.ts" };
        var r = FilesEndpoint.Rank(files, "", 10);
        Assert.Equal("top.ts", r[0]);          // 0 slashes = shallowest
        Assert.Equal("a/mid.ts", r[1]);        // 1 slash
        Assert.Equal("a/b/c/deep.ts", r[2]);   // deepest last
    }

    [Fact]
    public void Walk_PrunesBuildDirs_ReturnsForwardSlashRelPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, "README.md"), "x");
            File.WriteAllText(Path.Combine(root, "src", "app.ts"), "x");
            File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "index.js"), "x");
            File.WriteAllText(Path.Combine(root, "bin", "out.dll"), "x");
            File.WriteAllText(Path.Combine(root, ".git", "config"), "x");

            var rels = FilesEndpoint.WalkRelativeFiles(root, CancellationToken.None);

            Assert.Contains("README.md", rels);
            Assert.Contains("src/app.ts", rels);                         // forward slash
            Assert.DoesNotContain(rels, p => p.Contains("node_modules"));
            Assert.DoesNotContain(rels, p => p.StartsWith("bin/"));
            Assert.DoesNotContain(rels, p => p.Contains(".git"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
