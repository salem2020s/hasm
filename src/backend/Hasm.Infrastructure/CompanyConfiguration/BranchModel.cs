namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>Branch values in their stored (normalised) form. The code is normalised upper case and immutable once created.</summary>
public sealed record BranchData(
    string Code, string? NameAr, string? NameEn,
    string? AddressLine1, string? AddressLine2, string? City, string? Region, string? PostalCode, string? Phone, string? Email, string? Website);

/// <summary>Raw client input; validated and normalised before it is stored. On update the code must be absent or equal to the stored one.</summary>
public sealed record BranchInput(
    string? Code, string? NameAr, string? NameEn,
    string? AddressLine1, string? AddressLine2, string? City, string? Region, string? PostalCode, string? Phone, string? Email, string? Website);

/// <summary>
/// A branch as stored. <see cref="ActivationEpoch"/> advances on every activation-state change; branch-scoped access assignments (a later
/// checkpoint) are effective only while the branch is active AND the epoch they recorded equals this value.
/// <see cref="SeedMigration"/> is 4 for the main branch created by migration 0004 (its provenance), otherwise null.
/// </summary>
public sealed record BranchView(
    Guid Id, BranchData Data, bool IsActive, bool IsMain, long ActivationEpoch, short? SeedMigration, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

