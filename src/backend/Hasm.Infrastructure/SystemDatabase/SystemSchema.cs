namespace Hasm.Infrastructure.SystemDatabase;

/// <summary>
/// Logical-to-physical identifier map for the system database. Backend-internal only; never
/// exposed through APIs, clients or error reports. Physical names are fixed and committed:
/// they are never regenerated. Changing a value requires a new migration, not an edit here.
/// This is obfuscation for hygiene, not a security control.
/// </summary>
public static class SystemSchema
{
    public static class Company
    {
        public const string Table = "t_f446f4d0";
        public const string Id = "c_a9a73285";
        public const string PhysicalDatabaseName = "c_30fd1c11";
        public const string ProvisioningState = "c_08f915ea";
        public const string CreatedAtUtc = "c_15ceb585";
        public const string StateChangedAtUtc = "c_d806142f";

        public const string PrimaryKey = "k_78d272fe";
        public const string UniqueDatabaseName = "k_b3d5d2a2";
        public const string CheckState = "k_3aeb4060";
        public const string CheckDatabaseName = "k_debceeec";
        public const string GuardFunction = "f_c52b26ca";
        public const string GuardTrigger = "r_d395935d";
    }

    /// <summary>Non-business infrastructure table that records applied migrations.</summary>
    public const string MigrationJournalTable = "migration_journal";

    /// <summary>Every logical name with its physical identifier (for validation, backup and restore tooling).</summary>
    public static IReadOnlyDictionary<string, string> Map { get; } = new Dictionary<string, string>
    {
        ["Company"] = Company.Table,
        ["Company.Id"] = Company.Id,
        ["Company.PhysicalDatabaseName"] = Company.PhysicalDatabaseName,
        ["Company.ProvisioningState"] = Company.ProvisioningState,
        ["Company.CreatedAtUtc"] = Company.CreatedAtUtc,
        ["Company.StateChangedAtUtc"] = Company.StateChangedAtUtc,
        ["Company.PrimaryKey"] = Company.PrimaryKey,
        ["Company.UniqueDatabaseName"] = Company.UniqueDatabaseName,
        ["Company.CheckState"] = Company.CheckState,
        ["Company.CheckDatabaseName"] = Company.CheckDatabaseName,
        ["Company.GuardFunction"] = Company.GuardFunction,
        ["Company.GuardTrigger"] = Company.GuardTrigger,
    };

    public static string Resolve(string logicalName) =>
        Map.TryGetValue(logicalName, out var physical)
            ? physical
            : throw new KeyNotFoundException("Unknown logical schema name.");
}
