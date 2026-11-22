using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Backup;

/// <summary>The kind of a backup destination (D-183): descriptive only (reports and the volume check); it changes no copy rule.</summary>
public enum BackupDestinationKind { Local, Network }

/// <summary>
/// One configured backup destination (Phase 4.3 slice 8, D-183): a folder the operating system reaches (a local folder or a mounted network share; HASM stores no credentials) that receives
/// byte-for-byte copies of complete, signed, encrypted runs (D-182). <see cref="Id"/> names it in reports and audit events, never its path. <see cref="DailyKeep"/> and <see cref="WeeklyKeep"/> may only
/// RAISE the retention minimums (D-132); <see cref="MaxAgeHours"/> is the freshness limit of its copies for the status (default: the status call's own limit).
/// </summary>
public sealed record BackupDestination(string Id, string Path, BackupDestinationKind Kind, int? DailyKeep, int? WeeklyKeep, int? MaxAgeHours)
{
    public string KindName => Kind == BackupDestinationKind.Local ? "local" : "network";
}

/// <summary>A destination after the safety checks of D-184: its resolved path, whether that folder exists as a real directory, and whether it shares the primary root's volume.</summary>
public sealed record CheckedDestination(BackupDestination Destination, string ResolvedPath, bool Exists, bool SameVolumeAsPrimary);

/// <summary>
/// The backup destinations of slice 8 (D-183, D-184): the strict parser of <c>Hasm:Backup:Destinations</c> and the safety checks every command runs before it copies, deletes or reports anything.
/// Fail closed: an invalid entry is never skipped, clamped or dropped (<c>destination_config_invalid</c>, <c>retention_below_minimum</c>; exit 3).
/// </summary>
public static partial class BackupDestinations
{
    public const string SectionName = "Hasm:Backup:Destinations";
    public const int MaximumDestinations = 8;
    public const int MaximumAgeHours = 8760;
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase) { "Id", "Path", "Kind", "DailyKeep", "WeeklyKeep", "MaxAgeHours" };

    [GeneratedRegex("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>
    /// The configured destinations, in entry order; none configured is an empty list (today's single-root behaviour). Refused with <c>destination_config_invalid</c> (the cause names the entry index
    /// and the rule, never a path): more than <see cref="MaximumDestinations"/> entries; entry keys that are not exactly 0..n-1 (a gap would drop an entry silently); an entry or field that is not a plain
    /// value; an unknown field (a misspelt <c>DailyKeep</c> must not fall back to the minimum); a missing, malformed or duplicate <c>Id</c>; a <c>Path</c> that is not absolute or carries surrounding
    /// white space; a <c>Kind</c> other than <c>local</c> or <c>network</c>; an optional field that is empty, not a plain decimal integer or out of range. <c>DailyKeep</c> below 7 or <c>WeeklyKeep</c>
    /// below 4 is <c>retention_below_minimum</c> (never clamped).
    /// </summary>
    public static IReadOnlyList<BackupDestination> FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (section.Value is not null) throw Invalid("the setting is not a list");
        var entries = section.GetChildren().ToList();
        if (entries.Count == 0) return [];
        if (entries.Count > MaximumDestinations) throw Invalid($"more than {MaximumDestinations} destinations");
        var byIndex = new SortedDictionary<int, IConfigurationSection>();
        foreach (var e in entries)
        {
            if (!int.TryParse(e.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var i) || i.ToString(CultureInfo.InvariantCulture) != e.Key || i >= entries.Count || !byIndex.TryAdd(i, e))
                throw Invalid("the entries are not numbered 0 to n-1 without gaps");
        }
        var result = new List<BackupDestination>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (i, e) in byIndex)
        {
            if (e.Value is not null) throw Invalid($"entry {i} is not an object");
            var fields = e.GetChildren().ToList();
            foreach (var f in fields)
                if (!Fields.Contains(f.Key) || f.Value is null || f.GetChildren().Any()) throw Invalid($"entry {i} has an unknown or structured field");
            string? Get(string name) => fields.FirstOrDefault(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase))?.Value;
            var id = Get("Id");
            if (id is null || !IdPattern().IsMatch(id)) throw Invalid($"entry {i} has no valid Id");
            if (!ids.Add(id)) throw Invalid($"entry {i} repeats an Id");
            var path = Get("Path");
            if (path is null || path.Length == 0 || path.Trim() != path || !System.IO.Path.IsPathFullyQualified(path)) throw Invalid($"destination {id} has no absolute Path");
            var kind = Get("Kind") switch
            {
                "local" => BackupDestinationKind.Local,
                "network" => BackupDestinationKind.Network,
                _ => throw Invalid($"destination {id} has no valid Kind (local or network)"),
            };
            int? Optional(string name, int min, int max)
            {
                var raw = Get(name);
                if (raw is null) return null;
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v > max) throw Invalid($"destination {id} has an invalid {name}");
                if (v < min) throw name is "DailyKeep" or "WeeklyKeep" ? new BackupException("retention_below_minimum", $"destination {id}: {name}") : Invalid($"destination {id} has an invalid {name}");
                return v;
            }
            result.Add(new BackupDestination(id, path, kind,
                Optional("DailyKeep", RetentionOptions.MinimumDaily, RetentionOptions.MaximumKeep),
                Optional("WeeklyKeep", RetentionOptions.MinimumWeekly, RetentionOptions.MaximumKeep),
                Optional("MaxAgeHours", 1, MaximumAgeHours)));
        }
        return result;
    }

    private static BackupException Invalid(string cause) => new("destination_config_invalid", cause);

    /// <summary>
    /// The safety checks of D-184, on resolved paths, before anything is copied, deleted or reported. Read-only: nothing is created here (a command that writes creates the folder owner-only itself).
    /// Refused, in this order, per destination:
    /// <list type="bullet">
    /// <item><c>symbolic_link_refused</c>: the destination path or any existing folder on it is a symbolic link (or junction), also a dangling one;</item>
    /// <item><c>destination_duplicate</c>: it resolves to the primary root or to another destination's folder;</item>
    /// <item><c>destination_overlaps_source</c>: it lies inside, or contains, the primary root, another destination or one of <paramref name="protectedDirectories"/> (the attachment root and the
    /// encryption, audit and signing key directories, the slice 5 set).</item>
    /// </list>
    /// <paramref name="primaryRoot"/> null: a command without a primary (for example <c>backup-keys</c>); the primary checks and the volume comparison are skipped.
    /// <paramref name="volumeOf"/> is the volume probe (<see cref="VolumeOf"/> by default); an unknown volume counts as the primary's (it cannot be shown to be independent).
    /// </summary>
    public static IReadOnlyList<CheckedDestination> Check(IReadOnlyList<BackupDestination> destinations, string? primaryRoot, IEnumerable<string?> protectedDirectories,
        Func<string, string?>? volumeOf = null)
    {
        volumeOf ??= VolumeOf;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? primary = null;
        if (primaryRoot is not null)
        {
            if (!System.IO.Path.IsPathFullyQualified(primaryRoot)) throw new BackupException("path_not_absolute");
            primary = BackupSet.RealPath(primaryRoot);
        }
        var protectedResolved = protectedDirectories.Where(p => p is not null && System.IO.Path.IsPathFullyQualified(p)).Select(p => BackupSet.RealPath(p!)).ToList();
        var resolved = new List<string>();
        foreach (var d in destinations)
        {
            if (LinkOnPath(d.Path)) throw new BackupException("symbolic_link_refused", $"destination {d.Id}");
            resolved.Add(BackupSet.RealPath(d.Path));
        }
        for (var i = 0; i < destinations.Count; i++)
        {
            var id = destinations[i].Id;
            if (primary is not null && string.Equals(resolved[i], primary, comparison)) throw new BackupException("destination_duplicate", $"destination {id}");
            for (var j = 0; j < i; j++)
                if (string.Equals(resolved[i], resolved[j], comparison)) throw new BackupException("destination_duplicate", $"destination {id}");
        }
        for (var i = 0; i < destinations.Count; i++)
        {
            var others = protectedResolved.Concat(resolved.Where((_, j) => j != i));
            if (primary is not null) others = others.Append(primary);
            foreach (var other in others)
                if (Overlap(resolved[i], other)) throw new BackupException("destination_overlaps_source", $"destination {destinations[i].Id}");
        }
        var primaryVolume = primary is null ? null : volumeOf(NearestExisting(primary));
        return [.. destinations.Select((d, i) => new CheckedDestination(d, resolved[i], Directory.Exists(resolved[i]) && !File.Exists(resolved[i]),
            primary is not null && (primaryVolume is null || volumeOf(NearestExisting(resolved[i])) is not { } v || string.Equals(v, primaryVolume, StringComparison.Ordinal))))];
    }

    /// <summary>
    /// D-184 from the other side: a folder another command writes (a key export, a trial-restore attachment target) must not lie inside or contain any configured destination
    /// (<c>destination_in_backup_root</c>), so recovery material never travels with the data copies (D-087) and a restore never writes into a copy.
    /// </summary>
    public static void RefuseInsideDestination(string folder, IReadOnlyList<BackupDestination> destinations)
    {
        var resolved = BackupSet.RealPath(folder);
        foreach (var d in destinations)
            if (Overlap(resolved, BackupSet.RealPath(d.Path))) throw new BackupException("destination_in_backup_root", $"destination {d.Id}");
    }

    /// <summary>True when <paramref name="path"/> resolves to the folder of one of <paramref name="destinations"/> (then a status call on it is a destination's root status, D-186).</summary>
    public static bool IsDestinationPath(string path, IReadOnlyList<BackupDestination> destinations)
    {
        var resolved = BackupSet.RealPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return destinations.Any(d => string.Equals(BackupSet.RealPath(d.Path), resolved, comparison));
    }

    private static bool Overlap(string a, string b) => BackupSet.IsInside(a, b) || BackupSet.IsInside(b, a);

    /// <summary>True when the path itself or any folder on it is a symbolic link or junction (also a dangling one). Reads metadata only.</summary>
    internal static bool LinkOnPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full) ?? "";
        var cur = root;
        foreach (var part in full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = System.IO.Path.Combine(cur, part);
            if (new FileInfo(cur).LinkTarget is not null || new DirectoryInfo(cur).LinkTarget is not null) return true;
        }
        return false;
    }

    /// <summary>The path itself when it exists, otherwise its nearest existing ancestor (a destination not created yet lies on its parent's volume).</summary>
    internal static string NearestExisting(string path)
    {
        var cur = System.IO.Path.GetFullPath(path);
        while (!Directory.Exists(cur) && !File.Exists(cur))
        {
            var parent = System.IO.Path.GetDirectoryName(cur);
            if (parent is null || parent == cur) return cur;
            cur = parent;
        }
        return cur;
    }

    /// <summary>
    /// An identifier of the volume (file system) that holds <paramref name="path"/>, or null when it cannot be determined. Linux: the <c>major:minor</c> device of the mount that holds the path
    /// (<c>/proc/self/mountinfo</c>, longest mount point that contains it). Windows: the volume root (<c>GetVolumePathName</c>; a UNC share is its own volume). Elsewhere: null.
    /// </summary>
    public static string? VolumeOf(string path)
    {
        try
        {
            var real = BackupSet.RealPath(path);
            if (OperatingSystem.IsLinux()) return LinuxDevice(real, File.ReadAllLines("/proc/self/mountinfo"));
            if (OperatingSystem.IsWindows())
            {
                var buffer = new char[1024];
                if (!GetVolumePathNameW(real, buffer, buffer.Length)) return null;
                var end = Array.IndexOf(buffer, '\0');
                return "vol:" + new string(buffer, 0, end < 0 ? buffer.Length : end).ToUpperInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BackupException) { }
        return null;
    }

    /// <summary>The device (<c>dev:major:minor</c>) of the mount in <paramref name="mountInfo"/> (lines of <c>/proc/self/mountinfo</c>) whose mount point is the longest that contains <paramref name="path"/>.</summary>
    internal static string? LinuxDevice(string path, IEnumerable<string> mountInfo)
    {
        string? best = null, device = null;
        foreach (var line in mountInfo)
        {
            var f = line.Split(' ');
            if (f.Length < 5 || !f[2].Contains(':')) continue;
            var mountPoint = Unescape(f[4]);
            if (!BackupSet.IsInside(path, mountPoint)) continue;
            if (best is null || mountPoint.Length >= best.Length) { best = mountPoint; device = "dev:" + f[2]; }
        }
        return device;
    }

    /// <summary>mountinfo escapes space, tab, newline and backslash as three octal digits (<c>\040</c>).</summary>
    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && IsOctal(s, i + 1))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 3), 8));
                i += 3;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static bool IsOctal(string s, int at) => at + 3 <= s.Length && s.AsSpan(at, 3).IndexOfAnyExceptInRange('0', '7') < 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, [Out] char[] volumePathName, int bufferLength);
}
