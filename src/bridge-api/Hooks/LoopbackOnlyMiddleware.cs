using System.Net;
using System.Net.Sockets;

namespace CortexBridge.Api.Hooks;

/// <summary>
/// Defense-in-depth network check for /internal/*. Pairs with the hook-token Bearer
/// check inside each endpoint handler (the real auth boundary).
///
/// Originally loopback-only — that fit Phase 1 when hooks ran inside the same container
/// as the bridge. ADR-013 moved claude + hooks to the VPS host; the host's hook scripts
/// POST to the container, which sees the docker-bridge gateway IP rather than 127.0.0.1.
/// To keep the spirit of the network gate, we now accept loopback OR any RFC1918
/// private IP (covers docker bridges + host loopback binding 127.0.0.1:3000). The
/// container's port 3000 is bound only to the host's loopback per docker-compose.yml,
/// so external traffic still cannot reach /internal/*.
/// </summary>
public class LoopbackOnlyMiddleware
{
    private readonly RequestDelegate _next;

    public LoopbackOnlyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/internal/", StringComparison.Ordinal))
        {
            await _next(ctx);
            return;
        }

        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null || !IsTrustedSource(remote))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(
                """{"error":{"code":"hook.untrusted_source","message":"Internal endpoints accept loopback or RFC1918 sources only"}}""");
            return;
        }

        await _next(ctx);
    }

    private static bool IsTrustedSource(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) return true;
        // Kestrel on dual-stack `http://[::]` reports IPv4 clients as IPv4-mapped IPv6
        // (e.g. ::ffff:172.18.0.1 for a docker bridge gateway). Unwrap so the IPv4
        // RFC1918 check below sees the embedded address.
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12 — covers docker default bridge subnets (172.17.x, 172.18.x, ...)
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
        }
        else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 — IPv6 unique local
            var b = addr.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
        }
        return false;
    }
}
