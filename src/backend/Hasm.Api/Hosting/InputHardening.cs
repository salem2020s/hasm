using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Npgsql;

namespace Hasm.Api.Hosting;

/// <summary>
/// Shared input rule: a text value may never contain U+0000 (NUL). PostgreSQL cannot store it (error 22021), so a NUL that reached a database call used to surface as a false "service unavailable". Every query value
/// and every JSON string value of a request body is checked HERE, once, before any database access; a violation is the standard validation answer
/// <c>400 { "error": "invalid_request", "fields": [ { "field": name, "code": "invalid_value" } ] }</c>.
/// The body scan is bounded by the ENDPOINT'S OWN request-size limit (and by <see cref="MaxScannedBody"/>): the limit is applied before the first read, exactly as the endpoint would, so a body above it is refused with the
/// same 413 as without the guard; a body declared larger than the cap is not scanned at all; an undeclared (chunked) body is read in small pooled chunks and the scan stops at the cap. The scan is a STREAM over the
/// bytes: a body without NUL costs no allocation sized by the declared length. Non-JSON bodies (uploads) are not scanned; anything not scanned falls back to the defence in depth of <see cref="HasmExceptionMapping"/>.
/// </summary>
public static class InputHardening
{
    /// <summary>The largest JSON body the guard scans (every JSON endpoint takes a few kilobytes; its own limit is lower still).</summary>
    public const int MaxScannedBody = 1 << 20;

    private const int Chunk = 8 * 1024;
    private const int EscapeLength = 6;                                                                       // the JSON escape of NUL is "\u0000"

    public static IApplicationBuilder UseHasmNulCharacterGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        string? field = null;
        // (a NUL in the path itself never gets this far: Kestrel refuses such a request line)
        foreach (var (key, values) in request.Query)
            if (key.Contains('\0')) { field = "query"; break; }
            else if (values.Any(v => v?.Contains('\0') == true)) { field = key; break; }
        if (field is null && IsJsonBody(request)) field = await ScanBodyAsync(context);
        if (field is null) { await next(context); return; }
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error = "invalid_request", fields = new[] { new { field, code = "invalid_value" } } });
    });

    /// <summary>The first field holding a NUL in the JSON body, or null (none found, or the body was not scanned). Leaves the request body rewound and unchanged.</summary>
    private static async Task<string?> ScanBodyAsync(HttpContext context)
    {
        var request = context.Request;
        var limit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;
        var cap = (int)Math.Min(MaxScannedBody, limit ?? MaxScannedBody);
        if (request.ContentLength is { } declared && (declared <= 0 || declared > cap)) return null;          // empty, or larger than the cap: not scanned (the endpoint's limit judges it when it reads)
        // The endpoint's own limit keeps applying: set it NOW, before the first read (the endpoint middleware cannot change it after the body has been read).
        var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is not null && sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = limit;
        request.EnableBuffering(bufferThreshold: 64 * 1024);                                                   // memory up to 64 KiB, then a temporary file; rewound below

        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(Chunk + EscapeLength);
        try
        {
            var total = 0;
            var carry = 0;                                                                                     // bytes kept from the previous chunk so an escape split across two chunks is still found
            var found = false;
            while (total <= cap)
            {
                var n = await request.Body.ReadAsync(buffer.AsMemory(carry, Chunk), context.RequestAborted);
                if (n == 0) break;
                total += n;
                var window = buffer.AsSpan(0, carry + n);
                if (window.IndexOf((byte)0) >= 0 || window.IndexOf("\\u0000"u8) >= 0) found = true;
                carry = Math.Min(EscapeLength - 1, window.Length);
                window[^carry..].CopyTo(buffer);
            }
            request.Body.Position = 0;                                                                         // the endpoint reads the body again, unchanged
            if (total > cap || !found) return null;                                                            // beyond the cap: not scanned; nothing suspicious: nothing to parse
            var whole = new byte[total];                                                                       // only a body that CONTAINS a NUL candidate is ever held whole (at most the cap)
            var read = 0;
            while (read < total)
            {
                var n = await request.Body.ReadAsync(whole.AsMemory(read), context.RequestAborted);
                if (n == 0) break;
                read += n;
            }
            request.Body.Position = 0;
            return NulFieldIn(whole.AsSpan(0, read));
        }
        finally { pool.Return(buffer); }
    }

    private static bool IsJsonBody(HttpRequest r) =>
        (HttpMethods.IsPost(r.Method) || HttpMethods.IsPut(r.Method) || HttpMethods.IsPatch(r.Method)) && r.ContentType is { } ct
        && (ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) || ct.Contains("+json", StringComparison.OrdinalIgnoreCase));

    /// <summary>The name of the first property whose string value (or name) contains NUL, "body" for a bare string, or null when there is none or the JSON is malformed (the binder answers that itself).</summary>
    public static string? NulFieldIn(ReadOnlySpan<byte> json)
    {
        if (json.IndexOf("\\u0000"u8) < 0 && json.IndexOf((byte)0) < 0) return null;                          // no escaped or raw NUL anywhere: nothing to find
        var names = new Stack<string>();
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
        try
        {
            string current = "body";
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        current = reader.GetString() ?? "body";
                        if (current.Contains('\0')) return "body";
                        break;
                    case JsonTokenType.String:
                        if (reader.GetString()?.Contains('\0') == true) return current;
                        break;
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        names.Push(current);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (names.Count > 0) current = names.Pop();
                        break;
                }
            }
        }
        catch (JsonException) { /* malformed or too deep: the binder reports it */ }
        return null;
    }
}

/// <summary>Maps a database failure caused by the CALLER'S DATA to a client error. Only PostgreSQL error 22021 (a character the database cannot store, for example NUL) is mapped; connection, availability and every other database failure keep their 503.</summary>
public static class HasmExceptionMapping
{
    /// <summary>True when <paramref name="error"/> (or an exception it wraps) is a PostgreSQL 22021 "invalid byte sequence for encoding" failure. That condition can only come from the data of the statement, never from an outage.</summary>
    public static bool IsInvalidCharacter(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: PostgresErrorCodes.CharacterNotInRepertoire }) return true;
            if (e is AggregateException agg && agg.InnerExceptions.Any(IsInvalidCharacter)) return true;
        }
        return false;
    }
}
