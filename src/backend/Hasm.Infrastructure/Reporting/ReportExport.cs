using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;

namespace Hasm.Infrastructure.Reporting;

/// <summary>Business-audit event types of report exports (the 1000+ range of the business stream; never renumber).</summary>
public static class ReportAuditEvents
{
    public const short Exported = 1110;
    public const short ExportRefused = 1111;
    public const short ExportFailed = 1112;
    public const short StatementMappingSaved = 1113;
}

/// <summary>One output format. Renderers never decide what a user may see: they only draw a <see cref="ReportDocument"/> that the authorized query already produced.</summary>
public interface IReportRenderer
{
    string Format { get; }
    string ContentType { get; }
    string Extension { get; }
    /// <summary>Writes the document (only the selected columns, by index into <see cref="ReportDocument.Columns"/>) to <paramref name="output"/>. May stream; must not buffer the whole report.</summary>
    Task RenderAsync(ReportDocument document, IReadOnlyList<int> columns, Stream output, CancellationToken ct);
}

/// <summary>A finished export on a short-lived temporary file that is deleted when the response ends. Nothing is sent before the whole file exists, so a failure can never produce a partial download.</summary>
public sealed class ExportFile(Stream content, string fileName, string contentType) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public string FileName { get; } = fileName;
    public string ContentType { get; } = contentType;
    public long Length => Content.Length;
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record ReportExportRequest(Guid CompanyId, Guid UserId, ReportDocument Document, string Format, IReadOnlyList<string>? Columns, string FileStem);

/// <summary>
/// The one place where a report becomes a file: column selection, the format, the explicit row limit (refused BEFORE rendering, never a silent cut), a temporary file, and an audit record of every outcome
/// (success, refusal, failure) with the report identity, filters, format, row count and user, but never the report's contents. Authorization is the caller's job and happens before the document is built.
/// </summary>
public sealed class ReportExportService(CompanyAuditedTransactions transactions, ReportOptions options, IEnumerable<IReportRenderer> renderers)
{
    private readonly Dictionary<string, IReportRenderer> byFormat = renderers.ToDictionary(r => r.Format, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Formats => byFormat.Keys;

    public async Task<ConfigResult<ExportFile>> ExportAsync(ReportExportRequest request, CancellationToken ct = default)
    {
        var doc = request.Document;
        var format = (request.Format ?? "").Trim().ToLowerInvariant();
        if (!byFormat.TryGetValue(format, out var renderer)) return ConfigResult<ExportFile>.Invalid([new("format", "unsupported_format")]);

        var keys = request.Columns is { Count: > 0 } ? request.Columns : [.. doc.Columns.Where(c => c.Default).Select(c => c.Key)];
        var indexes = new List<int>();
        foreach (var key in keys)
        {
            var i = doc.Columns.ToList().FindIndex(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || indexes.Contains(i)) return ConfigResult<ExportFile>.Invalid([new("columns", "invalid_columns")]);
            indexes.Add(i);
        }
        if (indexes.Count == 0) return ConfigResult<ExportFile>.Invalid([new("columns", "invalid_columns")]);

        var limit = format == "pdf" ? options.MaxPdfRows : options.MaxExportRows;
        if (doc.RowCount > limit)
        {
            await AuditAsync(request, format, ReportAuditEvents.ExportRefused, doc.RowCount, 0, "refused_too_large", ct);
            return ConfigResult<ExportFile>.Fail(ConfigStatus.Conflict, "export_too_large");
        }

        var path = Path.Combine(options.TempDirectory ?? Path.GetTempPath(), "hasm-report-" + Guid.NewGuid().ToString("N") + ".tmp");
        FileStream file;
        try
        {
            file = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.DeleteOnClose | FileOptions.Asynchronous, BufferSize = 81920 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await AuditAsync(request, format, ReportAuditEvents.ExportFailed, doc.RowCount, 0, "failed_storage", ct);
            return ConfigResult<ExportFile>.Fail(ConfigStatus.Conflict, "export_unavailable");
        }
        try
        {
            await renderer.RenderAsync(doc, indexes, file, ct);
            await file.FlushAsync(ct);
            file.Position = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await file.DisposeAsync();
            await AuditAsync(request, format, ReportAuditEvents.ExportFailed, doc.RowCount, 0, "failed_" + ex.GetType().Name, ct);
            throw;
        }
        catch { await file.DisposeAsync(); throw; }
        await AuditAsync(request, format, ReportAuditEvents.Exported, doc.RowCount, file.Length, "ok", ct);
        return ConfigResult<ExportFile>.Ok(new ExportFile(file, $"{request.FileStem}.{renderer.Extension}", renderer.ContentType));
    }

    private async Task AuditAsync(ReportExportRequest r, string format, short eventType, long rows, long bytes, string outcome, CancellationToken ct)
    {
        var filters = r.Document.FiltersSummary.Length > 300 ? r.Document.FiltersSummary[..300] : r.Document.FiltersSummary;
        var detail = $"report={r.Document.ReportId} fmt={format} rows={rows.ToString(CultureInfo.InvariantCulture)} bytes={bytes.ToString(CultureInfo.InvariantCulture)} outcome={outcome} filters=[{filters}]";
        await transactions.ExecuteAsync(r.CompanyId, async work =>
        {
            await work.AppendAuditAsync(new AuditEventData(eventType, ActorUserId: r.UserId, Detail: detail), ct);
            return true;
        }, ct);
    }
}

/// <summary>CSV: UTF-8 with BOM (Arabic opens correctly), exact four-decimal amounts, ISO dates, and every cell that could start a spreadsheet formula neutralised. Data only (no decoration), streamed.</summary>
public sealed class CsvReportRenderer : IReportRenderer
{
    public string Format => "csv";
    public string ContentType => "text/csv; charset=utf-8";
    public string Extension => "csv";

    public static string Cell(string? v) =>
        v is null ? "" : v.Length > 0 && "=+-@\t\r".Contains(v[0]) ? "\"'" + v.Replace("\"", "\"\"") + "\"" : v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    /// <summary>The exact text of a value: decimals with four places, dates ISO, booleans yes/no, everything else as text.</summary>
    public static string Text(object? v, ReportColumnType type) => v switch
    {
        null => "",
        decimal d => type == ReportColumnType.Rate ? d.ToString("0.0000000000", CultureInfo.InvariantCulture) : type == ReportColumnType.Percent ? d.ToString("0.00", CultureInfo.InvariantCulture) : type == ReportColumnType.Money ? d.ToString("0.0000", CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "yes" : "no",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    public async Task RenderAsync(ReportDocument document, IReadOnlyList<int> columns, Stream output, CancellationToken ct)
    {
        await using var w = new StreamWriter(output, new UTF8Encoding(true), 65536, leaveOpen: true) { NewLine = "\r\n" };
        await w.WriteLineAsync(string.Join(',', columns.Select(i => Cell(document.Columns[i].TitleEn))));
        await foreach (var row in document.Rows(ct).WithCancellation(ct))
        {
            var cells = columns.Select(i =>
            {
                var type = document.Columns[i].Type;
                var text = Text(row.Cells[i], type);
                return type is ReportColumnType.Text or ReportColumnType.Code ? Cell(text) : text;
            });
            await w.WriteLineAsync(string.Join(',', cells));
        }
        await w.FlushAsync(ct);
    }
}

/// <summary>Helpers shared by the reports: a short stable hash of a filter text for diagnostics.</summary>
public static class ReportHash
{
    public static string Short(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
}
