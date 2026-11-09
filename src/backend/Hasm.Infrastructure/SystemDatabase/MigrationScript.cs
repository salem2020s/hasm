using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.SystemDatabase;

public sealed class MigrationException(string message) : Exception(message);

/// <summary>A forward-only, versioned SQL script embedded in the assembly.</summary>
public sealed partial record MigrationScript(int Version, string Name, string Sql)
{
    /// <summary>SHA-256 over the script with normalized line endings (git autocrlf-safe).</summary>
    public string Checksum { get; } =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.Replace("\r\n", "\n")))).ToLowerInvariant();

    [GeneratedRegex(@"^(\d{4})_([a-z0-9_]+)\.sql$")]
    private static partial Regex FileName();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\b(DROP|TRUNCATE)\b|\bDELETE\s+FROM\b", RegexOptions.IgnoreCase)]
    private static partial Regex Destructive();

    public static MigrationScript Parse(string fileName, string sql)
    {
        var m = FileName().Match(fileName);
        if (!m.Success) throw new MigrationException($"Invalid migration file name '{fileName}' (expected NNNN_name.sql).");
        var script = new MigrationScript(int.Parse(m.Groups[1].Value), fileName, sql);
        if (Destructive().IsMatch(LineComment().Replace(sql, "")))
            throw new MigrationException($"Migration {fileName} contains a destructive statement (DROP/TRUNCATE/DELETE); not allowed.");
        return script;
    }

    /// <summary>Loads embedded scripts ordered by version; versions must be unique and contiguous from 1.</summary>
    public static IReadOnlyList<MigrationScript> LoadEmbedded(Assembly? assembly = null) =>
        LoadEmbedded("Hasm.Infrastructure.SystemDatabase.Migrations.", assembly);

    /// <summary>Scripts of the minimal company-database schema, applied to every company database.</summary>
    public static IReadOnlyList<MigrationScript> LoadEmbeddedCompany() =>
        LoadEmbedded("Hasm.Infrastructure.CompanyDatabase.Migrations.", null);

    private static IReadOnlyList<MigrationScript> LoadEmbedded(string resourcePrefix, Assembly? assembly)
    {
        assembly ??= typeof(MigrationScript).Assembly;
        var scripts = new List<MigrationScript>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(resourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // Resource name: <namespace>.<file>; the file name is the last two dot-segments.
            var parts = resource.Split('.');
            scripts.Add(Parse($"{parts[^2]}.{parts[^1]}", reader.ReadToEnd()));
        }
        return Validate(scripts);
    }

    public static IReadOnlyList<MigrationScript> Validate(IEnumerable<MigrationScript> scripts)
    {
        var ordered = scripts.OrderBy(s => s.Version).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Version != i + 1)
                throw new MigrationException("Migration versions must be unique and contiguous starting at 0001.");
        }
        return ordered;
    }
}
