using Hasm.Infrastructure.Backup;
using Microsoft.Extensions.Configuration;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Phase 4.3 slice 8, S8a (D-183, D-184): the strict destination configuration parser and the safety checks on resolved paths (links, duplicates, overlaps in both directions, the volume probe).
/// </summary>
public sealed class BackupDestinationsTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "hasm-dst-" + Guid.NewGuid().ToString("N"));

    public BackupDestinationsTests() => Directory.CreateDirectory(work);
    public void Dispose() { try { Directory.Delete(work, true); } catch (IOException) { } }

    private static IReadOnlyList<BackupDestination> Parse(params (string Key, string Value)[] values) =>
        BackupDestinations.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => "Hasm:Backup:Destinations:" + v.Key, v => (string?)v.Value)).Build());

    private static string Refused(Action act) => Assert.Throws<BackupException>(act).Code;

    [Fact]
    public void Valid_destinations_are_parsed_in_entry_order_and_none_configured_is_an_empty_list()
    {
        Assert.Empty(BackupDestinations.FromConfiguration(new ConfigurationBuilder().Build()));
        var d = Parse(("0:Id", "nas-1"), ("0:Path", "/mnt/hasm-nas/backups"), ("0:Kind", "network"),
            ("1:Id", "usb-a"), ("1:Path", "/srv/hasm-usb/backups"), ("1:Kind", "local"), ("1:DailyKeep", "14"), ("1:WeeklyKeep", "4"), ("1:MaxAgeHours", "48"));
        Assert.Equal(["nas-1", "usb-a"], d.Select(x => x.Id));
        Assert.Equal(new BackupDestination("nas-1", "/mnt/hasm-nas/backups", BackupDestinationKind.Network, null, null, null), d[0]);
        Assert.Equal(new BackupDestination("usb-a", "/srv/hasm-usb/backups", BackupDestinationKind.Local, 14, 4, 48), d[1]);
        Assert.Equal("network", d[0].KindName);
    }

    [Fact]
    public void Environment_variables_with_double_underscores_are_read_like_the_json_form()
    {
        var name = "HASMTEST" + Guid.NewGuid().ToString("N")[..8] + "_";
        try
        {
            Environment.SetEnvironmentVariable(name + "Hasm__Backup__Destinations__0__Id", "nas-1");
            Environment.SetEnvironmentVariable(name + "Hasm__Backup__Destinations__0__Path", "/mnt/nas");
            Environment.SetEnvironmentVariable(name + "Hasm__Backup__Destinations__0__Kind", "network");
            var d = BackupDestinations.FromConfiguration(new ConfigurationBuilder().AddEnvironmentVariables(name).Build());
            Assert.Equal("nas-1", Assert.Single(d).Id);
        }
        finally
        {
            foreach (var f in new[] { "Id", "Path", "Kind" }) Environment.SetEnvironmentVariable(name + "Hasm__Backup__Destinations__0__" + f, null);
        }
    }

    public static TheoryData<string, (string, string)[]> InvalidConfigurations => new()
    {
        { "missing id", [("0:Path", "/a"), ("0:Kind", "local")] },
        { "upper-case id", [("0:Id", "NAS"), ("0:Path", "/a"), ("0:Kind", "local")] },
        { "id too long", [("0:Id", new string('a', 33)), ("0:Path", "/a"), ("0:Kind", "local")] },
        { "duplicate id", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("1:Id", "a"), ("1:Path", "/b"), ("1:Kind", "local")] },
        { "relative path", [("0:Id", "a"), ("0:Path", "backups"), ("0:Kind", "local")] },
        { "path with surrounding space", [("0:Id", "a"), ("0:Path", "/a "), ("0:Kind", "local")] },
        { "missing path", [("0:Id", "a"), ("0:Kind", "local")] },
        { "unknown kind", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "cloud")] },
        { "kind in upper case", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "Local")] },
        { "missing kind", [("0:Id", "a"), ("0:Path", "/a")] },
        { "misspelt field", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:DailyKep", "30")] },
        { "structured field", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:DailyKeep:x", "30")] },
        { "non-numeric keep", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:DailyKeep", "ten")] },
        { "signed keep", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:DailyKeep", "+10")] },
        { "empty optional field", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:MaxAgeHours", "")] },
        { "keep above the maximum", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:DailyKeep", "10001")] },
        { "max age zero", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:MaxAgeHours", "0")] },
        { "max age above a year", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:MaxAgeHours", "8761")] },
        { "gap in the entries", [("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("2:Id", "b"), ("2:Path", "/b"), ("2:Kind", "local")] },
        { "entry not numbered from zero", [("1:Id", "a"), ("1:Path", "/a"), ("1:Kind", "local")] },
        { "named entry", [("first:Id", "a"), ("first:Path", "/a"), ("first:Kind", "local")] },
        { "leading zero in the index", [("00:Id", "a"), ("00:Path", "/a"), ("00:Kind", "local")] },
        { "scalar entry", [("0", "/a")] },
        { "nine destinations", [.. Enumerable.Range(0, 9).SelectMany(i => new[] { ($"{i}:Id", $"d{i}"), ($"{i}:Path", $"/d{i}"), ($"{i}:Kind", "local") })] },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void An_invalid_entry_refuses_the_whole_configuration_and_is_never_skipped(string _, (string, string)[] values) =>
        Assert.Equal("destination_config_invalid", Refused(() => Parse(values)));

    [Fact]
    public void A_scalar_destinations_setting_is_refused() =>
        Assert.Equal("destination_config_invalid", Refused(() => BackupDestinations.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Hasm:Backup:Destinations"] = "/a" }).Build())));

    [Fact]
    public void Retention_below_the_minimums_is_refused_never_clamped_and_eight_destinations_are_allowed()
    {
        Assert.Equal("retention_below_minimum", Refused(() => Parse(("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:DailyKeep", "6"))));
        Assert.Equal("retention_below_minimum", Refused(() => Parse(("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:WeeklyKeep", "3"))));
        Assert.Equal(7, Parse(("0:Id", "a"), ("0:Path", "/a"), ("0:Kind", "local"), ("0:DailyKeep", "7"))[0].DailyKeep);
        Assert.Equal(8, Parse([.. Enumerable.Range(0, 8).SelectMany(i => new[] { ($"{i}:Id", $"d{i}"), ($"{i}:Path", $"/d{i}"), ($"{i}:Kind", "local") })]).Count);
    }

    private string Dir(string name) { var p = Path.Combine(work, name); Directory.CreateDirectory(p); return p; }
    private static BackupDestination D(string id, string path) => new(id, path, BackupDestinationKind.Local, null, null, null);
    private static string? SameVolume(string _) => "dev:1:1";

    [Fact]
    public void A_safe_destination_passes_and_a_missing_one_is_reported_not_created()
    {
        var primary = Dir("primary");
        var nas = Dir("nas");
        var later = Path.Combine(work, "later");
        var checkedDestinations = BackupDestinations.Check([D("nas", nas), D("later", later)], primary, [Dir("attachments")], p => p.Contains("nas") ? "dev:2:2" : "dev:1:1");
        Assert.True(checkedDestinations[0].Exists);
        Assert.False(checkedDestinations[0].SameVolumeAsPrimary);
        Assert.False(checkedDestinations[1].Exists);
        Assert.True(checkedDestinations[1].SameVolumeAsPrimary);           // not created yet: its parent's volume, here the primary's
        Assert.False(Directory.Exists(later));                            // read-only: nothing is created by the check
    }

    [Fact]
    public void Overlaps_in_either_direction_and_duplicates_are_refused()
    {
        var primary = Dir("primary");
        var keys = Dir("keys");
        string Code(BackupDestination[] d, params string[] protectedDirs) => Refused(() => BackupDestinations.Check(d, primary, protectedDirs, SameVolume));
        Assert.Equal("destination_duplicate", Code([D("a", primary)]));
        Assert.Equal("destination_duplicate", Code([D("a", primary + Path.DirectorySeparatorChar)]));                         // a trailing separator is the same folder
        Assert.Equal("destination_duplicate", Code([D("a", Dir("x")), D("b", Path.Combine(work, "x", ".", ""))]));
        Assert.Equal("destination_overlaps_source", Code([D("a", Path.Combine(primary, "copies"))]));                          // inside the primary
        Assert.Equal("destination_overlaps_source", Code([D("a", work)]));                                                    // contains the primary
        Assert.Equal("destination_overlaps_source", Code([D("a", Path.Combine(keys, "copies"))], keys));                      // inside a key directory
        Assert.Equal("destination_overlaps_source", Code([D("a", work + "-x"), D("b", Path.Combine(work + "-x", "inner"))]));  // inside another destination
        Assert.Equal("destination_overlaps_source", Code([D("a", Path.Combine(work + "-y", "inner")), D("b", work + "-y")]));  // and the other way round
        // a sibling with a common name prefix is not inside
        Assert.Single(BackupDestinations.Check([D("a", primary + "-copies")], primary, [keys], SameVolume));
        if (OperatingSystem.IsWindows())
            Assert.Equal("destination_duplicate", Code([D("a", primary.ToUpperInvariant())]));                               // case differences on Windows
    }

    [Fact]
    public void A_destination_through_a_symbolic_link_is_refused_also_a_dangling_one()
    {
        if (OperatingSystem.IsWindows()) return;   // creating a link needs a privilege there; the rule is the same
        var primary = Dir("primary");
        var real = Dir("real");
        var link = Path.Combine(work, "link"); Directory.CreateSymbolicLink(link, real);
        var dangling = Path.Combine(work, "dangling"); Directory.CreateSymbolicLink(dangling, Path.Combine(work, "gone"));
        string Code(string path) => Refused(() => BackupDestinations.Check([D("a", path)], primary, [], SameVolume));
        Assert.Equal("symbolic_link_refused", Code(link));
        Assert.Equal("symbolic_link_refused", Code(Path.Combine(link, "copies")));    // a link on the path
        Assert.Equal("symbolic_link_refused", Code(dangling));
        // a link that points INTO the primary is refused as a link before anything else
        var intoPrimary = Path.Combine(work, "into"); Directory.CreateSymbolicLink(intoPrimary, primary);
        Assert.Equal("symbolic_link_refused", Code(intoPrimary));
    }

    [Fact]
    public void An_unknown_volume_counts_as_the_primarys_because_independence_cannot_be_shown()
    {
        var primary = Dir("primary");
        var c = BackupDestinations.Check([D("a", Dir("a")), D("b", Dir("b"))], primary, [], p => p.EndsWith("primary", StringComparison.Ordinal) ? "dev:1:1" : p.EndsWith("a", StringComparison.Ordinal) ? null : "dev:9:9");
        Assert.True(c[0].SameVolumeAsPrimary);
        Assert.False(c[1].SameVolumeAsPrimary);
        var noPrimaryVolume = BackupDestinations.Check([D("b", Path.Combine(work, "b"))], primary, [], p => p.EndsWith("primary", StringComparison.Ordinal) ? null : "dev:9:9");
        Assert.True(noPrimaryVolume[0].SameVolumeAsPrimary);
        // without a primary (for example backup-keys) nothing is compared
        Assert.False(BackupDestinations.Check([D("b", Path.Combine(work, "b"))], null, [], _ => null)[0].SameVolumeAsPrimary);
    }

    [Fact]
    public void The_linux_volume_is_the_device_of_the_longest_mount_point_that_holds_the_path()
    {
        string[] mounts =
        [
            "22 1 0:21 / / rw,relatime - overlay overlay rw",
            "30 22 0:30 / /tmp rw - tmpfs tmpfs rw",
            "31 22 8:17 / /mnt/nas\\040share rw - cifs //server/share rw",
            "32 22 8:1 / /mnt rw - ext4 /dev/sda1 rw",
        ];
        Assert.Equal("dev:0:21", BackupDestinations.LinuxDevice("/srv/backups", mounts));
        Assert.Equal("dev:0:30", BackupDestinations.LinuxDevice("/tmp/x", mounts));
        Assert.Equal("dev:8:1", BackupDestinations.LinuxDevice("/mnt/usb", mounts));
        Assert.Equal("dev:8:17", BackupDestinations.LinuxDevice("/mnt/nas share/backups", mounts));   // an escaped space in the mount point
        Assert.Equal("dev:0:21", BackupDestinations.LinuxDevice("/tmpfoo", mounts));                // a common prefix is not the mount
        Assert.Null(BackupDestinations.LinuxDevice("/x", ["garbage"]));
        if (OperatingSystem.IsLinux()) Assert.NotNull(BackupDestinations.VolumeOf(work));
    }

    [Fact]
    public void A_key_export_or_trial_restore_folder_inside_or_around_a_destination_is_refused()
    {
        var nas = Dir("nas");
        IReadOnlyList<BackupDestination> configured = [D("nas", nas)];
        Assert.Equal("destination_in_backup_root", Refused(() => BackupDestinations.RefuseInsideDestination(Path.Combine(nas, "keys"), configured)));
        Assert.Equal("destination_in_backup_root", Refused(() => BackupDestinations.RefuseInsideDestination(nas, configured)));
        Assert.Equal("destination_in_backup_root", Refused(() => BackupDestinations.RefuseInsideDestination(work, configured)));
        BackupDestinations.RefuseInsideDestination(Path.Combine(work, "kit"), configured);    // beside it is fine
        BackupDestinations.RefuseInsideDestination(Path.Combine(work, "kit"), []);
        Assert.True(BackupDestinations.IsDestinationPath(nas + Path.DirectorySeparatorChar, configured));
        Assert.False(BackupDestinations.IsDestinationPath(work, configured));
    }
}
