using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Hosting;

/// <summary>
/// The request-body limit of an ACTION route: a state transition (activate, deactivate, submit, withdraw, approve, reject, cancel, post, sync, ...) carries no body at all or at most a short reason or note, so it is
/// limited to 16 KiB whatever the class-wide limit of its endpoints (which is sized for documents). The completeness test <c>RequestSizeLimitTests</c> pins "action routes at most this" so a new action route cannot
/// be given a document-sized limit by accident. Import actions keep the smaller 4 KiB limit of their class.
/// </summary>
public static class RequestLimits
{
    private sealed class Limit(long max) : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => max; }

    /// <summary>16 KiB: the limit of every action route (reuse this one instance with <c>.WithMetadata(RequestLimits.Action)</c>).</summary>
    public static IRequestSizeLimitMetadata Action { get; } = new Limit(16 * 1024);
}
