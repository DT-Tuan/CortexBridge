using System.Threading.Channels;

namespace CortexBridge.Api.Cortex;

/// <summary>
/// One save_memory job, enqueued by the endpoint and drained by
/// <see cref="CortexSaveWorker"/>. Carries no HTTP/DI state — just the data the
/// worker needs (+ the auth token id for the audit row). Never carries secrets.
/// </summary>
public record CortexSaveJob(
    string Content, string Scope, string Topic, string? Repository, double? Importance, long? TokenId);

/// <summary>
/// Process-wide queue for asynchronous memory saves (ADR-025 Slice 3, async
/// path). save_memory embeds the content on the CortexPlexus LXC and was
/// measured at 50–70 s under load — far too slow for a synchronous mobile
/// request, so the endpoint enqueues here and returns 202; the worker performs
/// the MCP call off the request path. Single-user, low volume → unbounded
/// channel, single reader.
/// </summary>
public class CortexSaveQueue
{
    private readonly Channel<CortexSaveJob> _ch =
        Channel.CreateUnbounded<CortexSaveJob>(new UnboundedChannelOptions { SingleReader = true });

    public bool Enqueue(CortexSaveJob job) => _ch.Writer.TryWrite(job);
    public ChannelReader<CortexSaveJob> Reader => _ch.Reader;
}
