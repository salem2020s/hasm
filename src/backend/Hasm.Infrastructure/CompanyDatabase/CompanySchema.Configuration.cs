namespace Hasm.Infrastructure.CompanyDatabase;

// Generated once for company migration 0003 (Phase 3.1A-2). Physical names are opaque and fixed: changing one requires a new migration.
public static partial class CompanySchema
{
    public static class Profile
    {
        public const string Table = "t_2e9d6bc3";
        public const string Singleton = "c_81d7eedc";
        public const string NameAr = "c_bf187da1";
        public const string NameEn = "c_a5039363";
        public const string LegalName = "c_997d18d9";
        public const string RegistrationNo = "c_a7358ff3";
        public const string TaxNo = "c_61272f02";
        public const string CountryCode = "c_ff810b4a";
        public const string Address1 = "c_fa7b1c20";
        public const string Address2 = "c_2aafdf1e";
        public const string City = "c_f500049e";
        public const string Region = "c_077dc782";
        public const string PostalCode = "c_5108587b";
        public const string Phone = "c_54f337be";
        public const string Email = "c_d4b4f263";
        public const string Website = "c_f6e709ac";
        public const string Revision = "c_ab911ce6";
        public const string UpdatedAtUtc = "c_099a7c10";
        public const string PrimaryKey = "k_2e0faa9f";
        public const string CheckSingleton = "k_7aaf5b2c";
        public const string CheckName = "k_05daa1e6";
        public const string CheckCountry = "k_abb696f4";
        public const string CheckRevision = "k_1d956de0";
        public const string GuardFunction = "f_8cb0c195";
        public const string GuardRow = "r_3b873b12";
        public const string GuardTruncate = "r_de094354";
    }

    public static class Settings
    {
        public const string Table = "t_91632385";
        public const string Singleton = "c_17f711ae";
        public const string DefaultLanguage = "c_6018782a";
        public const string DateFormat = "c_c50f3112";
        public const string NumeralSystem = "c_d707e413";
        public const string TimeZone = "c_05e0a481";
        public const string Revision = "c_958e8cfd";
        public const string UpdatedAtUtc = "c_bd5ed7d9";
        public const string PrimaryKey = "k_2b1c41d0";
        public const string CheckSingleton = "k_d02830a5";
        public const string CheckValues = "k_5c6c576e";
        public const string CheckRevision = "k_6d29eeea";
        public const string GuardFunction = "f_24d41a61";
        public const string GuardRow = "r_cae49e47";
        public const string GuardTruncate = "r_9ad749be";
    }

    public static class Currency
    {
        public const string Table = "t_451e91be";
        public const string Code = "c_b6064cf1";
        public const string NumericCode = "c_e4b41998";
        public const string NameEn = "c_0a598299";
        public const string MinorUnits = "c_b037f3d5";
        public const string IsActive = "c_f8d3a05a";
        public const string DatasetVersion = "c_4b13e59d";
        public const string PrimaryKey = "k_e1984a82";
        public const string UniqueNumeric = "k_c72623fe";
        public const string CheckValues = "k_091aedd2";
        public const string GuardFunction = "f_8bc68ad5";
        public const string GuardRow = "r_999aca37";
        public const string GuardTruncate = "r_6fee6c21";
    }

    public static class CompanyCurrency
    {
        public const string Table = "t_f5bc326a";
        public const string Code = "c_6da45b20";
        public const string IsBase = "c_5cdc1bc7";
        public const string Enabled = "c_702730ab";
        public const string Revision = "c_172cc0fb";
        public const string UpdatedAtUtc = "c_e6033338";
        public const string PrimaryKey = "k_b65971ee";
        public const string ForeignKeyCurrency = "k_fc23520c";
        public const string CheckBaseEnabled = "k_e521d50a";
        public const string CheckRevision = "k_2c5b6e22";
        public const string UniqueBase = "k_16957f38";
        public const string GuardFunction = "f_383a1156";
        public const string GuardRow = "r_d6b1ff2c";
        public const string GuardTruncate = "r_54661fe7";
    }

    public static class ConfigRevision
    {
        public const string Table = "t_28e40627";
        public const string Id = "c_1a60a888";
        public const string Entity = "c_63d71cfe";
        public const string EntityKey = "c_f9d1a1a0";
        public const string EntityRevision = "c_06d232c5";
        public const string ActorUserId = "c_f0e6a1e6";
        public const string ChangedAtUtc = "c_85d457aa";
        public const string Changes = "c_3136498c";
        public const string AuditStream = "c_fda83fac";
        public const string AuditSeq = "c_4cf0bc15";
        public const string PrimaryKey = "k_6027e53d";
        public const string UniqueEntityRevision = "k_ba3c64bd";
        public const string UniqueAuditEvent = "k_00a7d2d4";
        public const string ForeignKeyAudit = "k_f9a03b99";
        public const string CheckValues = "k_d6ad16bf";
        public const string GuardFunction = "f_add97941";
        public const string GuardRow = "r_e911d88f";
        public const string GuardTruncate = "r_e0b16d49";
    }

    private static IReadOnlyDictionary<string, string> ConfigurationMap => configurationMap.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> configurationMap = new(() => new Dictionary<string, string>
    {
        ["Profile"] = Profile.Table,
        ["Profile.Singleton"] = Profile.Singleton,
        ["Profile.NameAr"] = Profile.NameAr,
        ["Profile.NameEn"] = Profile.NameEn,
        ["Profile.LegalName"] = Profile.LegalName,
        ["Profile.RegistrationNo"] = Profile.RegistrationNo,
        ["Profile.TaxNo"] = Profile.TaxNo,
        ["Profile.CountryCode"] = Profile.CountryCode,
        ["Profile.Address1"] = Profile.Address1,
        ["Profile.Address2"] = Profile.Address2,
        ["Profile.City"] = Profile.City,
        ["Profile.Region"] = Profile.Region,
        ["Profile.PostalCode"] = Profile.PostalCode,
        ["Profile.Phone"] = Profile.Phone,
        ["Profile.Email"] = Profile.Email,
        ["Profile.Website"] = Profile.Website,
        ["Profile.Revision"] = Profile.Revision,
        ["Profile.UpdatedAtUtc"] = Profile.UpdatedAtUtc,
        ["Profile.PrimaryKey"] = Profile.PrimaryKey,
        ["Profile.CheckSingleton"] = Profile.CheckSingleton,
        ["Profile.CheckName"] = Profile.CheckName,
        ["Profile.CheckCountry"] = Profile.CheckCountry,
        ["Profile.CheckRevision"] = Profile.CheckRevision,
        ["Profile.GuardFunction"] = Profile.GuardFunction,
        ["Profile.GuardRow"] = Profile.GuardRow,
        ["Profile.GuardTruncate"] = Profile.GuardTruncate,
        ["Settings"] = Settings.Table,
        ["Settings.Singleton"] = Settings.Singleton,
        ["Settings.DefaultLanguage"] = Settings.DefaultLanguage,
        ["Settings.DateFormat"] = Settings.DateFormat,
        ["Settings.NumeralSystem"] = Settings.NumeralSystem,
        ["Settings.TimeZone"] = Settings.TimeZone,
        ["Settings.Revision"] = Settings.Revision,
        ["Settings.UpdatedAtUtc"] = Settings.UpdatedAtUtc,
        ["Settings.PrimaryKey"] = Settings.PrimaryKey,
        ["Settings.CheckSingleton"] = Settings.CheckSingleton,
        ["Settings.CheckValues"] = Settings.CheckValues,
        ["Settings.CheckRevision"] = Settings.CheckRevision,
        ["Settings.GuardFunction"] = Settings.GuardFunction,
        ["Settings.GuardRow"] = Settings.GuardRow,
        ["Settings.GuardTruncate"] = Settings.GuardTruncate,
        ["Currency"] = Currency.Table,
        ["Currency.Code"] = Currency.Code,
        ["Currency.NumericCode"] = Currency.NumericCode,
        ["Currency.NameEn"] = Currency.NameEn,
        ["Currency.MinorUnits"] = Currency.MinorUnits,
        ["Currency.IsActive"] = Currency.IsActive,
        ["Currency.DatasetVersion"] = Currency.DatasetVersion,
        ["Currency.PrimaryKey"] = Currency.PrimaryKey,
        ["Currency.UniqueNumeric"] = Currency.UniqueNumeric,
        ["Currency.CheckValues"] = Currency.CheckValues,
        ["Currency.GuardFunction"] = Currency.GuardFunction,
        ["Currency.GuardRow"] = Currency.GuardRow,
        ["Currency.GuardTruncate"] = Currency.GuardTruncate,
        ["CompanyCurrency"] = CompanyCurrency.Table,
        ["CompanyCurrency.Code"] = CompanyCurrency.Code,
        ["CompanyCurrency.IsBase"] = CompanyCurrency.IsBase,
        ["CompanyCurrency.Enabled"] = CompanyCurrency.Enabled,
        ["CompanyCurrency.Revision"] = CompanyCurrency.Revision,
        ["CompanyCurrency.UpdatedAtUtc"] = CompanyCurrency.UpdatedAtUtc,
        ["CompanyCurrency.PrimaryKey"] = CompanyCurrency.PrimaryKey,
        ["CompanyCurrency.ForeignKeyCurrency"] = CompanyCurrency.ForeignKeyCurrency,
        ["CompanyCurrency.CheckBaseEnabled"] = CompanyCurrency.CheckBaseEnabled,
        ["CompanyCurrency.CheckRevision"] = CompanyCurrency.CheckRevision,
        ["CompanyCurrency.UniqueBase"] = CompanyCurrency.UniqueBase,
        ["CompanyCurrency.GuardFunction"] = CompanyCurrency.GuardFunction,
        ["CompanyCurrency.GuardRow"] = CompanyCurrency.GuardRow,
        ["CompanyCurrency.GuardTruncate"] = CompanyCurrency.GuardTruncate,
        ["ConfigRevision"] = ConfigRevision.Table,
        ["ConfigRevision.Id"] = ConfigRevision.Id,
        ["ConfigRevision.Entity"] = ConfigRevision.Entity,
        ["ConfigRevision.EntityKey"] = ConfigRevision.EntityKey,
        ["ConfigRevision.EntityRevision"] = ConfigRevision.EntityRevision,
        ["ConfigRevision.ActorUserId"] = ConfigRevision.ActorUserId,
        ["ConfigRevision.ChangedAtUtc"] = ConfigRevision.ChangedAtUtc,
        ["ConfigRevision.Changes"] = ConfigRevision.Changes,
        ["ConfigRevision.AuditStream"] = ConfigRevision.AuditStream,
        ["ConfigRevision.AuditSeq"] = ConfigRevision.AuditSeq,
        ["ConfigRevision.PrimaryKey"] = ConfigRevision.PrimaryKey,
        ["ConfigRevision.UniqueEntityRevision"] = ConfigRevision.UniqueEntityRevision,
        ["ConfigRevision.UniqueAuditEvent"] = ConfigRevision.UniqueAuditEvent,
        ["ConfigRevision.ForeignKeyAudit"] = ConfigRevision.ForeignKeyAudit,
        ["ConfigRevision.CheckValues"] = ConfigRevision.CheckValues,
        ["ConfigRevision.GuardFunction"] = ConfigRevision.GuardFunction,
        ["ConfigRevision.GuardRow"] = ConfigRevision.GuardRow,
        ["ConfigRevision.GuardTruncate"] = ConfigRevision.GuardTruncate,
    });
}
