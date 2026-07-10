using System.Security.Cryptography;

namespace CortexBridge.Api.Hooks;

/// <summary>
/// Generates the bridge hook token at process startup, holds it in memory, and writes
/// it to /run/bridge-hook-token (mode 0600) so hook scripts can read it.
/// Spec 03 §2.5: bridge is the single owner of this token.
/// </summary>
public class HookTokenProvider
{
    public string? Token { get; private set; }
    private readonly string _tokenFilePath;

    public HookTokenProvider(IConfiguration config)
    {
        _tokenFilePath = config["BRIDGE_HOOK_TOKEN_FILE"]
            ?? Environment.GetEnvironmentVariable("BRIDGE_HOOK_TOKEN_FILE")
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Path.GetTempPath(), "bridge-hook-token")
                : "/run/bridge-hook-token");
    }

    public void InitializeOnStartup(ILogger logger)
    {
        if (Token is not null) return;

        var raw = RandomNumberGenerator.GetBytes(32);
        Token = Convert.ToHexStringLower(raw);

        try
        {
            var dir = Path.GetDirectoryName(_tokenFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_tokenFilePath, Token);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_tokenFilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            logger.LogInformation("Hook token generated and written to {Path}", _tokenFilePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write hook token file {Path}", _tokenFilePath);
            // Continue running — hooks just won't work until the file write succeeds.
        }
    }

    public bool Validate(string? presented) =>
        Token is not null
        && !string.IsNullOrEmpty(presented)
        && presented.Length == Token.Length
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(Token));
}
