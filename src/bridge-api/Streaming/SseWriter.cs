using System.Text;
using System.Text.Json;
using CortexBridge.Api.Common;

namespace CortexBridge.Api.Streaming;

/// <summary>
/// Per-connection serialized SSE writer. Multiple producers (the message-pump loop and the
/// keepalive timer) need to write to the same Response.Body without interleaving frames.
/// Fix #2: previously used as a static helper which allowed concurrent writes from the
/// keepalive Task and the message foreach loop, racing on Response.Body.
/// </summary>
public sealed class SseChannel
{
    private readonly HttpResponse _response;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SseChannel(HttpResponse response)
    {
        _response = response;
    }

    public async Task WriteEventAsync(string eventName, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data, Json.Default);
        var frame = $"event: {eventName}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(frame);
        await _writeLock.WaitAsync(ct);
        try
        {
            await _response.Body.WriteAsync(bytes, ct);
            await _response.Body.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// SSE comment line. Used as a keepalive — proxies / Caddy idle timeout typically 60s,
    /// so we flush a heartbeat every 25s.
    /// </summary>
    public async Task WriteKeepaliveAsync(CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(": keepalive\n\n");
        await _writeLock.WaitAsync(ct);
        try
        {
            await _response.Body.WriteAsync(bytes, ct);
            await _response.Body.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
