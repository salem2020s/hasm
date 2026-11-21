using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Hosting;

/// <summary>
/// The request-body limit of an ACTION route: a route without a document payload. A state transition (activate, deactivate, submit, withdraw, approve, reject, cancel, post, sync, ...) carries no body at all; a decision,
/// a reversal, a revocation, a release or a document link carries a short reason or note (at most 1000 characters), a date and a few ids. Such a route is limited to 16 KiB whatever the class-wide limit of its
/// endpoints (which is sized for documents). The largest legitimate body of the routes that use it is about 9 KiB (500 characters sent decomposed and with every code unit as a six-byte JSON escape). The completeness
/// test <c>RequestSizeLimitTests</c> pins the rule by what a route BINDS, not by its verb: a POST that binds no body, or a body without any collection, is at most this unless it is listed there with its reason, so a
/// new action route cannot be given a document-sized limit by accident. Import actions keep the smaller 4 KiB limit of their class.
/// </summary>
public static class RequestLimits
{
    private sealed class Limit(long max) : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => max; }

    /// <summary>16 KiB: the limit of every action route (reuse this one instance with <c>.WithMetadata(RequestLimits.Action)</c>).</summary>
    public static IRequestSizeLimitMetadata Action { get; } = new Limit(16 * 1024);
}
