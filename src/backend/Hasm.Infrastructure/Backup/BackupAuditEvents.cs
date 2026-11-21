namespace Hasm.Infrastructure.Backup;

/// <summary>
/// SYSTEM-stream audit event codes of Phase 4.3 slice 5 (D-118): the block 2010-2019, allocated by the lead after a registry check (the attachment retention block holds 2000-2009, commercial documents
/// 1900-1999; nothing used 2010-2019 on main or on any open branch). Persisted codes: never renumber. The detail carries codes, key ids, company ids and target-database TAGS only, never a physical name.
/// </summary>
public static class BackupAuditEvents
{
    /// <summary>A trial restore ran (D-118: a restore operation under D-080(6)).</summary>
    public const short TrialRestore = 2010;
    /// <summary>A backup recovery test ran (D-085/D-117).</summary>
    public const short RecoveryTest = 2011;
    /// <summary><c>backup-keys</c> exported the key directories (D-087/D-094).</summary>
    public const short KeysExported = 2012;
    /// <summary>A backup signing key was generated (D-092).</summary>
    public const short SigningKeyGenerated = 2013;
    // 2014-2019 reserved for later Phase 4.3 slices (retention, cutover)
}
