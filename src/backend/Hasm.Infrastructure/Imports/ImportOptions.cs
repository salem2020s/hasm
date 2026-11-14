using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Imports;

/// <summary>
/// Import limits and storage (section <c>Hasm:Imports</c>, environment <c>Hasm__Imports__*</c>). Every limit is a server-enforced, configurable default (never a permanent product limit);
/// an invalid or non-positive value falls back to the default rather than disabling the limit. The storage directory has no default: without it uploads fail closed.
/// </summary>
public sealed class ImportOptions
{
    public const string SectionName = "Hasm:Imports";

    /// <summary>Directory for the ENCRYPTED original files (outside any web root, owner-only access). Required.</summary>
    public string? StorageDirectory { get; init; }
    public long MaxFileBytes { get; init; } = 10L * 1024 * 1024;
    public int MaxRows { get; init; } = 50_000;
    public int MaxColumns { get; init; } = 200;
    public int MaxWorksheets { get; init; } = 20;
    public int MaxCellChars { get; init; } = 4_000;
    /// <summary>Total uncompressed size of all parts of a spreadsheet (zip-bomb protection) and of a decoded CSV text.</summary>
    public long MaxExpandedBytes { get; init; } = 100L * 1024 * 1024;
    public int MaxZipEntries { get; init; } = 200;
    public int MaxProcessingSeconds { get; init; } = 60;
    /// <summary>Upper bound a company may configure for pending-batch expiry and retention (days).</summary>
    public int MaxRetentionDays { get; init; } = 3650;

    public static ImportOptions FromConfiguration(IConfiguration configuration)
    {
        var d = new ImportOptions();
        string? Get(string key) => string.IsNullOrWhiteSpace(configuration[$"{SectionName}:{key}"]) ? null : configuration[$"{SectionName}:{key}"]!.Trim();
        long L(string key, long fallback) => long.TryParse(Get(key), out var v) && v > 0 ? v : fallback;
        int I(string key, int fallback) => int.TryParse(Get(key), out var v) && v > 0 ? v : fallback;
        return new ImportOptions
        {
            StorageDirectory = Get("StorageDirectory"),
            MaxFileBytes = L("MaxFileBytes", d.MaxFileBytes), MaxRows = I("MaxRows", d.MaxRows), MaxColumns = I("MaxColumns", d.MaxColumns), MaxWorksheets = I("MaxWorksheets", d.MaxWorksheets),
            MaxCellChars = I("MaxCellChars", d.MaxCellChars), MaxExpandedBytes = L("MaxExpandedBytes", d.MaxExpandedBytes), MaxZipEntries = I("MaxZipEntries", d.MaxZipEntries),
            MaxProcessingSeconds = I("MaxProcessingSeconds", d.MaxProcessingSeconds), MaxRetentionDays = I("MaxRetentionDays", d.MaxRetentionDays),
        };
    }
}

/// <summary>A refusal of an uploaded file or its staging. <see cref="Code"/> is a stable machine-readable reason; <see cref="Detail"/> is safe to show (never file content).</summary>
public sealed class ImportRejectedException(string code, string? detail = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}
