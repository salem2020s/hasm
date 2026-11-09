namespace Hasm.Infrastructure.CompanyDatabase;

/// <summary>
/// Logical-to-physical identifier map for the minimal schema of every company database.
/// Backend-internal only; never exposed through APIs or clients. Fixed and committed: changing a
/// value requires a new migration. Obfuscation for hygiene, not a security control (D-029).
/// </summary>
public static class CompanySchema
{
    /// <summary>Single-row table binding a company database to the company id of the central registry.</summary>
    public static class Identity
    {
        public const string Table = "t_209d0f15";
        public const string Singleton = "c_8709851b";
        public const string CompanyId = "c_2d7eb73b";
        public const string CreatedAtUtc = "c_a77d8875";
        public const string PrimaryKey = "k_bf65319b";
        public const string CheckSingleton = "k_8fd0da3b";
        public const string GuardFunction = "f_0b15f60c";
        public const string GuardTrigger = "r_ec68b932";
    }

    public static IReadOnlyDictionary<string, string> Map { get; } = new Dictionary<string, string>
    {
        ["Identity"] = Identity.Table,
        ["Identity.Singleton"] = Identity.Singleton,
        ["Identity.CompanyId"] = Identity.CompanyId,
        ["Identity.CreatedAtUtc"] = Identity.CreatedAtUtc,
        ["Identity.PrimaryKey"] = Identity.PrimaryKey,
        ["Identity.CheckSingleton"] = Identity.CheckSingleton,
        ["Identity.GuardFunction"] = Identity.GuardFunction,
        ["Identity.GuardTrigger"] = Identity.GuardTrigger,
    };
}
