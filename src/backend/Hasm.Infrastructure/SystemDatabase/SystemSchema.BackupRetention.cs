namespace Hasm.Infrastructure.SystemDatabase;

// Phase 4.3 slice 6 (D-134, D-135): backup legal holds and recovery-chain pins. The opaque names are DERIVED: prefix + the first 8 hex digits of SHA-256("hasm.system.backup-retention.v1:" + logical token)
// (re-derived by BackupRetentionSchemaDerivationTests). The seed carries no migration number, so the names stay the same when the migration takes the next free number at merge.
public static partial class SystemSchema
{
    /// <summary>A legal hold on backup runs: one run (scope 1) or one company's runs started within an optional local-date range (scope 2). Append-only; released once, never deleted.</summary>
    public static class BackupLegalHold
    {
        public const string Table = "t_4caa7490";
        public const string Id = "c_cac29687";
        public const string Scope = "c_1ea28b23";
        public const string RunId = "c_65d245cf";
        public const string CompanyId = "c_29d8b5b4";
        public const string FromDate = "c_a88bdba2";
        public const string ToDate = "c_5cb55291";
        public const string Reason = "c_d77f1c27";
        public const string PlacedBy = "c_a231fa62";
        public const string PlacedAtUtc = "c_5d6a8ce2";
        public const string ReleasedBy = "c_cc1f1452";
        public const string ReleasedAtUtc = "c_32d04987";
        public const string ReleaseReason = "c_d2976757";
        public const string Pk = "k_624dbb7d";
        public const string CkScope = "k_9ae8fd8d";
        public const string CkRelease = "k_b0ad7db5";
        public const string FnGuard = "f_af9948c7";
        public const string TrRow = "r_42e8f242";
        public const string TrTruncate = "r_c27e1c2c";
    }

    /// <summary>A pin of a run that an active recovery chain needs (for example the safety backup of a cutover, slice 7) until <see cref="ExpiresAtUtc"/>. Append-only.</summary>
    public static class BackupRetentionPin
    {
        public const string Table = "t_035c414c";
        public const string Id = "c_ed15eadd";
        public const string RunId = "c_b02f6b9d";
        public const string Kind = "c_b8353210";
        public const string Reason = "c_3813c927";
        public const string CreatedBy = "c_56cd5893";
        public const string CreatedAtUtc = "c_75514e76";
        public const string ExpiresAtUtc = "c_57a322de";
        public const string Pk = "k_2e07034e";
        public const string CkValues = "k_580dd48e";
        public const string FnGuard = "f_86d2edd8";
        public const string TrRow = "r_2b255a22";
        public const string TrTruncate = "r_107d8e66";
    }

    private static IReadOnlyDictionary<string, string> BackupRetentionMap => backupRetentionMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> backupRetentionMap = new(() => new Dictionary<string, string>
    {
        ["BackupLegalHold"] = BackupLegalHold.Table,
        ["BackupLegalHold.Id"] = BackupLegalHold.Id,
        ["BackupLegalHold.Scope"] = BackupLegalHold.Scope,
        ["BackupLegalHold.RunId"] = BackupLegalHold.RunId,
        ["BackupLegalHold.CompanyId"] = BackupLegalHold.CompanyId,
        ["BackupLegalHold.FromDate"] = BackupLegalHold.FromDate,
        ["BackupLegalHold.ToDate"] = BackupLegalHold.ToDate,
        ["BackupLegalHold.Reason"] = BackupLegalHold.Reason,
        ["BackupLegalHold.PlacedBy"] = BackupLegalHold.PlacedBy,
        ["BackupLegalHold.PlacedAtUtc"] = BackupLegalHold.PlacedAtUtc,
        ["BackupLegalHold.ReleasedBy"] = BackupLegalHold.ReleasedBy,
        ["BackupLegalHold.ReleasedAtUtc"] = BackupLegalHold.ReleasedAtUtc,
        ["BackupLegalHold.ReleaseReason"] = BackupLegalHold.ReleaseReason,
        ["BackupLegalHold.Pk"] = BackupLegalHold.Pk,
        ["BackupLegalHold.CkScope"] = BackupLegalHold.CkScope,
        ["BackupLegalHold.CkRelease"] = BackupLegalHold.CkRelease,
        ["BackupLegalHold.FnGuard"] = BackupLegalHold.FnGuard,
        ["BackupLegalHold.TrRow"] = BackupLegalHold.TrRow,
        ["BackupLegalHold.TrTruncate"] = BackupLegalHold.TrTruncate,
        ["BackupRetentionPin"] = BackupRetentionPin.Table,
        ["BackupRetentionPin.Id"] = BackupRetentionPin.Id,
        ["BackupRetentionPin.RunId"] = BackupRetentionPin.RunId,
        ["BackupRetentionPin.Kind"] = BackupRetentionPin.Kind,
        ["BackupRetentionPin.Reason"] = BackupRetentionPin.Reason,
        ["BackupRetentionPin.CreatedBy"] = BackupRetentionPin.CreatedBy,
        ["BackupRetentionPin.CreatedAtUtc"] = BackupRetentionPin.CreatedAtUtc,
        ["BackupRetentionPin.ExpiresAtUtc"] = BackupRetentionPin.ExpiresAtUtc,
        ["BackupRetentionPin.Pk"] = BackupRetentionPin.Pk,
        ["BackupRetentionPin.CkValues"] = BackupRetentionPin.CkValues,
        ["BackupRetentionPin.FnGuard"] = BackupRetentionPin.FnGuard,
        ["BackupRetentionPin.TrRow"] = BackupRetentionPin.TrRow,
        ["BackupRetentionPin.TrTruncate"] = BackupRetentionPin.TrTruncate,
    });
}
