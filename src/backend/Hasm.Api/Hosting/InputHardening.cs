using System.Text.Json;
using Npgsql;

namespace Hasm.Api.Hosting;

/// <summary>
/// Shared input rule: a text value may never contain U+0000 (NUL). PostgreSQL cannot store it (error 22021), so a NUL that reached a database call used to surface as a false "service unavailable". Every query value
/// and every JSON string value of a request body is checked HERE, once, before routing and before any database access; a violation is the standard validation answer
/// <c>400 { "error": "invalid_request", "fields": [ { "field": name, "code": "invalid_value" } ] }</c>. Bodies larger than <see cref="MaxScannedBody"/>, chunked bodies and non-JSON bodies (uploads) are not scanned here; they fall
/// back to the defence in depth of <see cref="HasmExceptionMapping"/>.
/// </summary>
public static class InputHardening
{
    /// <summary>The largest JSON body the guard buffers and scans (every JSON endpoint takes a few kilobytes; the largest body limit of the API is far above this only for file uploads, which are not JSON).</summary>
    public const int MaxScannedBody = 1 << 20;

    public static IApplicationBuilder UseHasmNulCharacterGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        string? field = null;
        // (a NUL in the path itself never gets this far: Kestrel refuses such a request line)
        foreach (var (key, values) in request.Query)
            if (key.Contains('\0')) { field = "query"; break; }
            else if (values.Any(v => v?.Contains('\0') == true)) { field = key; break; }
        if (field is null && IsJsonBody(request) && request.ContentLength is > 0 and <= MaxScannedBody)
        {
            request.EnableBuffering(MaxScannedBody);
            var buffer = new byte[request.ContentLength.Value];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await request.Body.ReadAsync(buffer.AsMemory(read), context.RequestAborted);
                if (n == 0) break;
                read += n;
            }
            request.Body.Position = 0;                                                                        // the endpoint reads the body again, unchanged
            field = NulFieldIn(buffer.AsSpan(0, read));
        }
        if (field is null) { await next(context); return; }
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error = "invalid_request", fields = new[] { new { field, code = "invalid_value" } } });
    });

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
