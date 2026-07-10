using System.Diagnostics;

namespace CortexBridge.Api.Tmux;

/// <summary>
/// All tmux invocations go through here. argv array — never shell concat.
/// Send-reply uses load-buffer/paste-buffer/send-keys per spec 03 §3.2 to handle
/// multi-line, UTF-8, IME-composed text safely (bracketed paste).
/// </summary>
public class TmuxClient
{
    private readonly ILogger<TmuxClient> _log;
    private readonly string _tmuxSessionName;
    private readonly string _tmuxBinary;

    public TmuxClient(IConfiguration config, ILogger<TmuxClient> log)
    {
        _log = log;
        _tmuxSessionName = config["BRIDGE_TMUX_SESSION"]
            ?? Environment.GetEnvironmentVariable("BRIDGE_TMUX_SESSION")
            ?? "cc";
        _tmuxBinary = config["BRIDGE_TMUX_BIN"]
            ?? Environment.GetEnvironmentVariable("BRIDGE_TMUX_BIN")
            ?? "tmux";
    }

    public string SessionName => _tmuxSessionName;

    public async Task<List<string>> ListWindowsAsync(CancellationToken ct)
    {
        var (stdout, _) = await RunAsync(
            new[] { "list-windows", "-t", _tmuxSessionName, "-F", "#W" }, null, ct);
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    public async Task<bool> WindowExistsAsync(string windowName, CancellationToken ct)
    {
        try
        {
            var windows = await ListWindowsAsync(ct);
            return windows.Any(w => string.Equals(w, windowName, StringComparison.OrdinalIgnoreCase));
        }
        catch (TmuxException)
        {
            return false;
        }
    }

    /// <summary>
    /// Inject text + Enter into tmux:session:window via load-buffer / paste-buffer / send-keys.
    /// Multi-line, UTF-8, IME-composed safe.
    ///
    /// CALLERS: do NOT call this method directly from endpoints / background
    /// services — use <c>TmuxReplyExtensions.SendReplyWithPickerDismissAsync</c>
    /// instead. That helper wraps the picker-dismiss dance the bridge MUST
    /// run before every paste, or CC silently swallows the message when a
    /// permission picker / AskUserQuestion is open. SendReplyAsync stays
    /// public so the extension can compose it and for the rare lower-level
    /// callsite that has a verified-idle pane.
    /// </summary>
    public async Task SendReplyAsync(string windowName, string text, CancellationToken ct)
    {
        var bufferName = $"cb-reply-{Guid.NewGuid():N}";
        var target = $"{_tmuxSessionName}:{windowName}";

        // Step 1: load-buffer reads from stdin
        await RunAsync(new[] { "load-buffer", "-b", bufferName, "-" }, stdinText: text, ct);

        // Step 2: paste-buffer with -d (delete after paste)
        await RunAsync(new[] { "paste-buffer", "-d", "-b", bufferName, "-t", target }, null, ct);

        // Step 3: send Enter (tmux key token, not user-controlled)
        await RunAsync(new[] { "send-keys", "-t", target, "Enter" }, null, ct);
    }

    public async Task SendKeysAsync(string windowName, string keys, CancellationToken ct)
    {
        var target = $"{_tmuxSessionName}:{windowName}";
        await RunAsync(new[] { "send-keys", "-t", target, keys }, null, ct);
    }

    /// <summary>
    /// Select an option in a CC interactive menu (tool-permission prompt,
    /// AskUserQuestion, folder-trust) by sending the option's DIGIT as a RAW
    /// keystroke — never via load-buffer/paste-buffer.
    ///
    /// Empirically verified against claude 2.1.140: a CC select-menu reads number
    /// keys as direct accelerators — a single "1" confirms option 1 immediately,
    /// no Enter. The paste path (SendReplyAsync) wraps text in bracketed-paste
    /// escapes (ESC[200~ … ESC[201~) which a menu can't interpret and treats as a
    /// cancel → CC records "[Request interrupted by user for tool use]" instead of
    /// the choice (the approve→interrupt→stuck-thinking bug).
    /// </summary>
    public async Task SendMenuChoiceAsync(string windowName, string digit, CancellationToken ct)
    {
        if (digit.Length != 1 || digit[0] is < '1' or > '9')
            throw new TmuxException($"menu choice must be a single digit 1-9 (got '{digit}')");
        var target = $"{_tmuxSessionName}:{windowName}";
        await RunAsync(new[] { "send-keys", "-t", target, digit }, null, ct);
    }

    /// <summary>
    /// Create a new tmux window with the given name + cwd, running `command` (single-string,
    /// e.g. "claude" or "claude --resume &lt;uuid&gt;"). tmux interprets command via /bin/sh -c.
    ///
    /// The command is run through a LOGIN shell (`bash -lc 'exec …'`). The bridge's tmux
    /// client lives INSIDE the container; a window it opens on the host tmux server inherits
    /// a minimal PATH (/usr/bin:…) WITHOUT ~/.local/bin. After the root /usr/bin/claude was
    /// removed in favour of the native ~/.local/bin/claude install, a bare `claude` then
    /// resolves to nothing and the window dies the instant it spawns ("resumed" per the API
    /// but immediately gone). A login shell sources the host profile so ~/.local/bin is on
    /// PATH; `exec` replaces the shell with claude so it stays the pane's foreground process
    /// (clean exit + accurate CrashWatcher). Verified: container-client spawn of
    /// `bash -lc 'command -v claude'` resolves ~/.local/bin/claude; a bare one does not.
    /// </summary>
    public async Task NewWindowAsync(string windowName, string cwd, string command, CancellationToken ct)
    {
        // Ensure base session exists (idempotent — fails harmlessly if it already does)
        try
        {
            await RunAsync(new[] { "new-session", "-d", "-s", _tmuxSessionName, "-x", "200", "-y", "50" }, null, ct);
        }
        catch (TmuxException) { /* already exists, fine */ }

        // Single-quote the command for the login shell; escape any embedded quotes.
        var loginCmd = $"bash -lc 'exec {command.Replace("'", "'\\''")}'";
        await RunAsync(
            new[] { "new-window", "-t", _tmuxSessionName, "-n", windowName, "-c", cwd, loginCmd },
            null, ct);
    }

    public async Task KillWindowAsync(string windowName, CancellationToken ct)
    {
        var target = $"{_tmuxSessionName}:{windowName}";
        try
        {
            await RunAsync(new[] { "kill-window", "-t", target }, null, ct);
        }
        catch (TmuxException ex)
        {
            _log.LogDebug(ex, "kill-window failed for {Target} (likely already gone)", target);
        }
    }

    /// <summary>
    /// Send a single named key to a tmux window via send-keys (e.g. "Escape", "Enter",
    /// "Tab", "Up"). Used by PWA to dismiss interactive pickers. Whitelist-validated.
    /// </summary>
    public async Task SendKeyAsync(string windowName, string key, CancellationToken ct)
    {
        // Whitelist tmux key names — refuse anything else to avoid injection of strings
        // like "/clear" via this path. Free-text reply uses load-buffer/paste-buffer.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Escape", "Enter", "Tab", "BTab",
            "Up", "Down", "Left", "Right",
            "PageUp", "PageDown", "Home", "End",
            "Space", "BSpace", "DC"
        };
        if (!allowed.Contains(key))
            throw new TmuxException($"send-key: key '{key}' not in whitelist");

        var target = $"{_tmuxSessionName}:{windowName}";
        await RunAsync(new[] { "send-keys", "-t", target, key }, null, ct);
    }

    /// <summary>
    /// Read the visible tmux pane text (read-only). Used to mirror CC's LIVE
    /// interactive menu into the PWA/ext banner so it shows the REAL options
    /// (Bash vs Edit-allow-all vs N-option) instead of a static 1/2/3 guess.
    ///
    /// <paramref name="historyLines"/> &gt; 0 prepends that many lines of
    /// scrollback (capture-pane -S -N) so an assistant analysis that has
    /// scrolled above the visible top is still captured — needed to recover the
    /// AskUserQuestion context prose (CC withholds the whole assistant message
    /// from JSONL until the question is answered, so the pane is the only live
    /// source). Default 0 = visible pane only.
    /// </summary>
    public async Task<string> CapturePaneAsync(string windowName, CancellationToken ct, int historyLines = 0)
    {
        var target = $"{_tmuxSessionName}:{windowName}";
        var args = historyLines > 0
            ? new[] { "capture-pane", "-p", "-S", $"-{historyLines}", "-t", target }
            : new[] { "capture-pane", "-p", "-t", target };
        var (stdout, _) = await RunAsync(args, null, ct);
        return stdout;
    }

    private async Task<(string stdout, string stderr)> RunAsync(
        string[] args, string? stdinText, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_tmuxBinary)
        {
            RedirectStandardInput = stdinText != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process p;
        try
        {
            p = Process.Start(psi)
                ?? throw new TmuxException("Failed to start tmux process");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new TmuxException(
                $"tmux binary '{_tmuxBinary}' not found or not executable: {ex.Message}", ex);
        }
        using var _ = p;

        if (stdinText is not null)
        {
            await p.StandardInput.WriteAsync(stdinText.AsMemory(), ct);
            p.StandardInput.Close();
        }

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (p.ExitCode != 0)
        {
            _log.LogWarning("tmux {Args} exited {Exit}: {Err}",
                string.Join(' ', args), p.ExitCode, stderr.Trim());
            throw new TmuxException($"tmux exit {p.ExitCode}: {stderr.Trim()}");
        }

        return (stdout, stderr);
    }
}

public class TmuxException : Exception
{
    public TmuxException(string message) : base(message) { }
    public TmuxException(string message, Exception inner) : base(message, inner) { }
}
