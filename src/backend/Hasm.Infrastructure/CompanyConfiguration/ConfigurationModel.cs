namespace Hasm.Infrastructure.CompanyConfiguration;

public enum ConfigStatus { Ok, Forbidden, Invalid, PreconditionRequired, PreconditionFailed, EntitlementDenied, Conflict, NotFound }

public sealed record FieldError(string Field, string Code);

/// <summary>Outcome of a configuration operation. <see cref="CurrentRevision"/> is set on a concurrency failure so clients can reload.</summary>
public sealed record ConfigResult<T>(ConfigStatus Status, string? Code, T? Value, IReadOnlyList<FieldError>? Fields = null, long? CurrentRevision = null)
{
    public bool IsOk => Status == ConfigStatus.Ok;
    public static ConfigResult<T> Ok(T value) => new(ConfigStatus.Ok, null, value);
    public static ConfigResult<T> Fail(ConfigStatus status, string code, long? currentRevision = null) => new(status, code, default, null, currentRevision);
    public static ConfigResult<T> Invalid(IReadOnlyList<FieldError> fields) => new(ConfigStatus.Invalid, "validation_failed", default, fields);
}

/// <summary>Business audit event types of company configuration (the 1000+ range of the business stream).</summary>
public static class ConfigAuditEvents
{
    public const short ProfileCreated = 1000;
    public const short ProfileUpdated = 1001;
    public const short SettingsCreated = 1002;
    public const short SettingsUpdated = 1003;
    public const short CurrencyEnabled = 1004;
    public const short CurrencyDisabled = 1005;
    public const short BaseCurrencySet = 1006;
    // Branch management (Phase 3.1B). The main branch created by migration 0004 has no event (it is not a user action).
    public const short BranchCreated = 1007;
    public const short BranchUpdated = 1008;
    public const short BranchDeactivated = 1009;
    public const short BranchReactivated = 1010;
    public const short BranchMainDesignated = 1011;
    public const short BranchMainRevoked = 1012;
    // Warehouse management (Phase 3.1C).
    public const short WarehouseCreated = 1013;
    public const short WarehouseUpdated = 1014;
    public const short WarehouseDeactivated = 1015;
    public const short WarehouseReactivated = 1016;
    // Fiscal calendar (Phase 3.1D).
    public const short FiscalYearCreated = 1017;
    public const short FiscalYearClosed = 1018;
    public const short FiscalYearReopened = 1019;
    public const short FiscalPeriodSoftClosed = 1020;
    public const short FiscalPeriodReopened = 1021;
    public const short FiscalPeriodFinalClosed = 1022;
    public const short FiscalPeriodFinalReopened = 1023;
}

/// <summary>Company profile values in their stored (normalised) form. Authoritative in the company database.</summary>
public sealed record ProfileData(
    string? NameAr, string? NameEn, string? LegalName, string? CommercialRegistrationNo, string? TaxRegistrationNo, string CountryCode,
    string? AddressLine1, string? AddressLine2, string? City, string? Region, string? PostalCode, string? Phone, string? Email, string? Website);

public sealed record ProfileView(ProfileData? Data, long Revision, DateTimeOffset? UpdatedAtUtc, string Projection);

/// <summary>Raw client input; every field is validated and normalised before it is stored.</summary>
public sealed record ProfileInput(
    string? NameAr, string? NameEn, string? LegalName, string? CommercialRegistrationNo, string? TaxRegistrationNo, string? CountryCode,
    string? AddressLine1, string? AddressLine2, string? City, string? Region, string? PostalCode, string? Phone, string? Email, string? Website);

public sealed record SettingsData(string DefaultLanguage, string DateFormat, string NumeralSystem, string? TimeZone)
{
    /// <summary>The documented defaults used until the first write. The business time zone is NEVER defaulted.</summary>
    public static SettingsData Defaults { get; } = new("ar", "yyyy-MM-dd", "latin", null);
}

public sealed record SettingsInput(string? DefaultLanguage, string? DateFormat, string? NumeralSystem, string? TimeZone);

/// <summary><see cref="Exists"/> false = defaults shown, nothing stored yet (a read never creates a row). Setup is complete only once a time zone is set.</summary>
public sealed record SettingsView(SettingsData Data, long Revision, bool Exists, bool SetupComplete, DateTimeOffset? UpdatedAtUtc);

public sealed record CurrencyReference(string Code, string NumericCode, string NameEn, short? MinorUnits, bool IsActive, int DatasetVersion);
public sealed record CompanyCurrencyView(string Code, string NameEn, short? MinorUnits, bool IsBase, bool Enabled, long Revision);
public sealed record LockInformation(bool Locked, bool ChecksImplemented, IReadOnlyList<string> UnimplementedDomains);
public sealed record CurrenciesView(IReadOnlyList<CompanyCurrencyView> Currencies, string? BaseCurrency, bool BaseCurrencySelected, LockInformation BaseCurrencyLock);

public sealed record ConfigHistoryEntry(long Id, string Entity, string EntityKey, long EntityRevision, Guid ActorUserId, DateTimeOffset ChangedAtUtc, string ChangesJson, long AuditSeq);

/// <summary>Picks the company name for a language, falling back to whichever name exists.</summary>
public static class CompanyNames
{
    public static string? Display(string? nameAr, string? nameEn, string language)
    {
        var preferred = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? nameEn : nameAr;
        var other = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? nameAr : nameEn;
        return !string.IsNullOrWhiteSpace(preferred) ? preferred : (!string.IsNullOrWhiteSpace(other) ? other : null);
    }
}

/// <summary>Thrown when a protected operation needs the company business time zone that has not been selected yet (incomplete setup).</summary>
public sealed class CompanyTimeZoneNotConfiguredException() : InvalidOperationException("The company time zone has not been configured.")
{
    public const string Code = "company_time_zone_not_configured";
}
