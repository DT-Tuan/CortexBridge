using CortexBridge.Api.Auth;
using CortexBridge.Api.Data;
using CortexBridge.Api.Endpoints;
using CortexBridge.Api.Hooks;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Usage;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ----- Config & paths -----
builder.Services.AddSingleton<BridgePaths>();
builder.Services.AddSingleton<UsagePaths>();
builder.Services.AddSingleton<UsageService>();
builder.Services.AddHostedService<UsagePoller>();

// ----- EF Core / SQLite -----
var dataDir = builder.Configuration["BRIDGE_DATA_DIR"]
    ?? Environment.GetEnvironmentVariable("BRIDGE_DATA_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var sqlitePath = Path.Combine(dataDir, "cortexbridge.db");
builder.Services.AddDbContext<BridgeDbContext>(opt =>
    opt.UseSqlite($"Data Source={sqlitePath}"));

// ----- Auth -----
builder.Services.AddScoped<TokenIssuer>();
builder.Services.AddSingleton<StreamTokenStore>();
builder.Services.AddSingleton<TokenRateLimiter>();

// ----- Hooks plumbing (token generation runs at startup) -----
builder.Services.AddSingleton<HookTokenProvider>();

// ----- Session scanning + parsing -----
builder.Services.AddSingleton<SessionScanner>();
builder.Services.AddSingleton<JsonlReader>();
builder.Services.AddSingleton<SessionStateRegistry>();
// Self-healing: clears Processing latched true by a slash command/skill that
// fired an activity hook but never a terminal Stop (see ProcessingWatchdog).
builder.Services.AddHostedService<ProcessingWatchdog>();
// /btw parity: 1-slot-per-project buffer for replies submitted while CC is
// busy; flusher pastes when processing flips false (or drops on TTL/interrupt).
builder.Services.AddSingleton<SessionQueue>();
builder.Services.AddHostedService<SessionQueueFlusher>();
builder.Services.AddSingleton<SessionOwnershipRegistry>();
// ADR-017: automatic Mode A/B via the Anthropic ide lockfile (no manual switch).
builder.Services.AddSingleton<IdeLockReader>();
builder.Services.AddHostedService<ModeWatcher>();
// ADR-022 Option Β — detection-only crash recovery (Web Push, no auto-spawn).
builder.Services.AddHostedService<CrashWatcher>();
builder.Services.AddSingleton<WatcherRegistry>();
builder.Services.AddSingleton<GitInspector>();

// ----- tmux client + per-project mutexes (reply + resume) -----
builder.Services.AddSingleton<CortexBridge.Api.Tmux.TmuxClient>();
builder.Services.AddSingleton<CortexBridge.Api.Tmux.ProjectReplyMutex>();
builder.Services.AddSingleton<CortexBridge.Api.Tmux.ProjectResumeMutex>();

// ----- CortexPlexus MCP client (ADR-025 Phase 4) — the bridge's FIRST outbound
//       HTTP dependency; LAN-only, fail-soft, off any hot path -----
// Address comes from config (env `CortexPlexus__McpUrl` or appsettings). It is
// deployment-specific, so there is no sane default: an unset value resolves to a
// .invalid host, which cannot resolve, and the client's existing fail-soft path
// degrades the cockpit instead of dialling someone else's LAN.
var cortexMcpUrl = builder.Configuration["CortexPlexus:McpUrl"];
CortexBridge.Api.Cortex.CortexRepoMap.Configure(builder.Configuration);
builder.Services.AddHttpClient<CortexBridge.Api.Cortex.ICortexPlexusClient,
    CortexBridge.Api.Cortex.CortexPlexusClient>(c =>
{
    c.BaseAddress = new Uri(string.IsNullOrWhiteSpace(cortexMcpUrl)
        ? "http://cortexplexus.invalid/mcp"
        : cortexMcpUrl);
    // 120 s ceiling: list/forget return in ms; save_memory embeds the content
    // (50–70 s on the contended LXC) and runs ONLY in CortexSaveWorker, off the
    // request path. recall_memory (30–50 s) is not called from the bridge.
    c.Timeout = TimeSpan.FromSeconds(120);
});
// Async memory-save queue + worker (ADR-025 Slice 3): save is too slow to await
// on a mobile request, so the endpoint enqueues and the worker drains.
builder.Services.AddSingleton<CortexBridge.Api.Cortex.CortexSaveQueue>();
builder.Services.AddHostedService<CortexBridge.Api.Cortex.CortexSaveWorker>();

// ----- Web Push (VAPID-signed) — replaces ntfy per ADR-012 -----
builder.Services.AddSingleton<WebPushSender>();

// ----- CORS for the VS Code companion webview (ADR-015) -----
// The webview's fetch + EventSource originate from a webview origin
// (https://*.vscode-webview.net or https://*.vscode-cdn.net). The PWA itself
// is served same-origin from wwwroot so doesn't need CORS, but the companion
// extension's webview is cross-origin. Bearer auth means cookies aren't in
// play, so allowing any origin with credentials disabled is safe.
const string CorsPolicy = "cortexbridge-webview";
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("Content-Type")));

// ----- Logging (ADR-019: Serilog provider; MEL-level config preserved) -----
// Serilog plugs in behind Microsoft.Extensions.Logging — every existing
// ILogger<T>/CreateLogger(name)/app.Logger call site is unchanged. Levels are
// read from the SAME `Logging:LogLevel` config (env-overridable), so the
// `Logging__LogLevel__Default=Debug` acceptance lever (ADR-016 watchdog
// recipe) keeps working with no new operator knowledge. Sink/format only —
// WHAT is logged is unchanged; the "never log message content" rule stands.
static Serilog.Events.LogEventLevel ToSerilog(string? mel) => mel switch
{
    "Trace" => Serilog.Events.LogEventLevel.Verbose,
    "Debug" => Serilog.Events.LogEventLevel.Debug,
    "Information" => Serilog.Events.LogEventLevel.Information,
    "Warning" => Serilog.Events.LogEventLevel.Warning,
    "Error" => Serilog.Events.LogEventLevel.Error,
    "Critical" or "None" => Serilog.Events.LogEventLevel.Fatal,
    _ => Serilog.Events.LogEventLevel.Information,
};
var logLevels = builder.Configuration.GetSection("Logging:LogLevel");
var logDir = Path.Combine(dataDir, "logs");
Directory.CreateDirectory(logDir);
builder.Logging.ClearProviders();
builder.Services.AddSerilog((_, lc) => lc
    .MinimumLevel.Is(ToSerilog(logLevels["Default"]))
    .MinimumLevel.Override("Microsoft.AspNetCore",
        ToSerilog(logLevels["Microsoft.AspNetCore"] ?? "Warning"))
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore",
        ToSerilog(logLevels["Microsoft.EntityFrameworkCore"] ?? "Warning"))
    .Enrich.FromLogContext()
    // Console: single-line, same yyyy-MM-ddTHH:mm:ss.fff timestamp so the
    // grep-based acceptance recipe and the operator's eye keep working.
    .WriteTo.Console(outputTemplate:
        "{Timestamp:yyyy-MM-ddTHH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    // Durable rolling file on the bind-mounted /data volume (survives
    // container recreate). Compact JSON for machine queryability; bounded by
    // daily roll + 50 MB size cap + 14 retained files to contain growth.
    .WriteTo.File(
        formatter: new Serilog.Formatting.Compact.CompactJsonFormatter(),
        path: Path.Combine(logDir, "bridge-.log"),
        rollingInterval: RollingInterval.Day,
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: 50L * 1024 * 1024,
        retainedFileCountLimit: 14));

var app = builder.Build();

// ----- Startup: ensure DB, generate hook token, write file -----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
    db.Database.EnsureCreated();

    // Manual "migration" for tables added after the initial EnsureCreated. EF Core's
    // EnsureCreated() only creates the schema if NO tables exist. For new entities
    // added later (spec 04: session_labels), run the CREATE TABLE explicitly so
    // upgrades on existing deployments pick up the new tables.
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS session_labels (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id  TEXT NOT NULL,
            session_uuid TEXT NOT NULL,
            label       TEXT NULL,
            note        TEXT NULL,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_session_labels_proj_uuid
            ON session_labels (project_id, session_uuid);

        -- ADR-015 / spec 05: per-project ownership marker. Persist only the EXPLICIT
        -- 'pc' handoff. 'tmux' and 'none' are derived from tmux window existence.
        CREATE TABLE IF NOT EXISTS session_ownership (
            project_id        TEXT PRIMARY KEY,
            owner             TEXT NOT NULL,
            session_uuid      TEXT NULL,
            since_utc         TEXT NOT NULL,
            changed_by_client TEXT NULL
        );

        -- Phase 3 usage tracking: one row per UsagePoller tick (5 min) + dedup
        -- table for threshold-crossing Web Push alerts. See Usage/UsagePoller.cs.
        CREATE TABLE IF NOT EXISTS usage_snapshots (
            id                       INTEGER PRIMARY KEY AUTOINCREMENT,
            taken_utc                TEXT NOT NULL,
            block5h_id               TEXT NOT NULL DEFAULT '',
            block5h_current_usd      TEXT NOT NULL DEFAULT '0',
            block5h_projected_usd    TEXT NOT NULL DEFAULT '0',
            block5h_pct_current      TEXT NOT NULL DEFAULT '0',
            block5h_pct_projected    TEXT NOT NULL DEFAULT '0',
            week7d_period            TEXT NOT NULL DEFAULT '',
            week7d_current_usd       TEXT NOT NULL DEFAULT '0',
            week7d_pct_current       TEXT NOT NULL DEFAULT '0'
        );
        CREATE INDEX IF NOT EXISTS ix_usage_snapshots_taken_utc
            ON usage_snapshots (taken_utc);

        CREATE TABLE IF NOT EXISTS usage_alerts_sent (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            window_kind   TEXT NOT NULL,
            window_id     TEXT NOT NULL,
            threshold_pct INTEGER NOT NULL,
            sent_utc      TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_usage_alerts_sent_dedup
            ON usage_alerts_sent (window_kind, window_id, threshold_pct);

        -- ADR-024: quota % now comes from the official OAuth usage endpoint
        -- (sampled host-side); the cap subsystem is gone. Drop the legacy
        -- table on deployments that had it.
        DROP TABLE IF EXISTS usage_caps;
    ");

    var hookProvider = scope.ServiceProvider.GetRequiredService<HookTokenProvider>();
    hookProvider.InitializeOnStartup(app.Logger);
}

// ----- Request logging (ADR-019) — one structured line per request -----
// /health is polled constantly; drop it below the Information floor so it
// doesn't drown real events. The query string is NOT enriched, so the SSE
// ?t=<stream-token> and ?endpoint= never reach a log.
app.UseSerilogRequestLogging(o =>
{
    o.GetLevel = (http, _, ex) =>
        ex is not null ? Serilog.Events.LogEventLevel.Error
        : http.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
});

// ----- Middleware order -----
// CORS must come BEFORE the bearer middleware so OPTIONS preflights — which
// browsers send without an Authorization header — are answered with the
// Access-Control-* headers instead of being rejected as auth.missing_token.
app.UseCors(CorsPolicy);
app.UseMiddleware<LoopbackOnlyMiddleware>();   // gates /internal/*
app.UseMiddleware<BearerTokenMiddleware>();    // gates /api/*

// ----- Static files (PWA wwwroot) + SPA fallback -----
// In dev, wwwroot may be empty; in prod the Docker stage copies pwa/build there.
// Cache strategy:
//   - /_app/immutable/* — content-hashed by SvelteKit → cache forever (immutable)
//   - everything else (index.html, manifest.webmanifest, service-worker.js,
//     icons) — no-store so CDN/CF doesn't serve stale shell after redeploy.
//     Without this, Cloudflare Tunnel caches /index.html and PWA loads old
//     asset hashes even after a fresh SW install.
app.UseDefaultFiles();
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? string.Empty;
        var headers = ctx.Context.Response.Headers;
        if (path.StartsWith("/_app/immutable/", StringComparison.Ordinal))
        {
            headers["Cache-Control"] = "public, max-age=31536000, immutable";
        }
        else
        {
            headers["Cache-Control"] = "no-store, must-revalidate";
        }
    }
});

// ----- Endpoints -----
app.MapHealth();
app.MapUsage();
app.MapAuth(builder.Configuration);
app.MapTokenAdmin();   // ADR-020: self-service token list/revoke/rotate
app.MapSessions();
app.MapStream();
app.MapReply();
app.MapInterrupt();   // PWA stop button: 2× ESC parity with CC CLI ESC
app.MapQueue();       // PWA queue mode: /btw parity (buffer while busy, flush on idle)
app.MapResume();
app.MapActivate();   // ADR-016 Slice 2: explicit lifecycle (kill+resume UID)
app.MapHandoff();
app.MapPrompt();
app.MapProjects();
app.MapProjectSessions();
app.MapFiles();   // GET .../files?q= — backs the PWA composer "@" file-mention picker
app.MapSyncPull();
app.MapPushSubscribe();
app.MapRestart();
app.MapExit();   // explicit "Thoat phien" lifecycle (graceful /exit + clear ownership)
app.MapNewSession();   // start fresh `claude` (no --resume) - kills any live window first
app.MapCommands();
app.MapAutoAllow();   // per-project flag for the host auto-allow PreToolUse hook
app.MapCortexUsage();   // ADR-025 Phase 4 Slice 1: transcript-derived cortexplexus MCP-usage badge
app.MapCortexMemory();   // ADR-025 Phase 4 Slice 2: memory cockpit (read-only) over MCP
app.MapInternalHooks(builder.Configuration);

// SPA fallback — any non-/api/*, non-/internal/* GET that didn't hit a static file -> index.html
app.MapFallback(async (HttpContext ctx) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/api/", StringComparison.Ordinal) ||
        path.StartsWith("/internal/", StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    var webRoot = app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
    var indexPath = Path.Combine(webRoot, "index.html");
    if (File.Exists(indexPath))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store, must-revalidate";
        await ctx.Response.SendFileAsync(indexPath);
    }
    else
    {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsync("PWA build not present in wwwroot. Run frontend build, or use the dev SvelteKit server on :5173.");
    }
});

app.Run();
