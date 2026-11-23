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
    /// <summary>Slice 6 (D-139): a retention plan was computed (preview).</summary>
    public const short RetentionEvaluated = 2014;
    /// <summary>Slice 6: the deletion of a run started (written BEFORE any file is touched).</summary>
    public const short DeletionStarted = 2015;
    /// <summary>Slice 6: a run was deleted (tombstone written).</summary>
    public const short Deleted = 2016;
    /// <summary>Slice 6: a deletion was refused (gate inactive, prerequisites, brake, or a run's re-validation).</summary>
    public const short DeletionRefused = 2017;
    /// <summary>Slice 6: a backup legal hold was placed.</summary>
    public const short LegalHoldPlaced = 2018;
    /// <summary>Slice 6: reserved for the release of a backup legal hold (not available until the Phase 4.1 approval framework).</summary>
    public const short LegalHoldReleased = 2019;
    // 2020-2029: allocated by the lead to slice 7 (cutover)
    // 2040-2049: slice 8 (multiple backup destinations, D-188). 2044-2049 are reserved.
    /// <summary>Slice 8 (D-188): a copy was verified by read-back after flush; written BEFORE the rename, so it records "publish attempted" and is never proof by itself that a copy exists.</summary>
    public const short ReplicaVerified = 2040;
    /// <summary>Slice 8: a copy failed (or a publish failed after 2040, the compensating event).</summary>
    public const short ReplicaFailed = 2041;
    /// <summary>Slice 8 (S8c): re-verification found a copy that no longer verifies (never repaired in place).</summary>
    public const short ReplicaCorrupt = 2042;
    /// <summary>Slice 8: a destination was refused before anything was written.</summary>
    public const short DestinationRefused = 2043;
}
