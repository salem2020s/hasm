namespace Hasm.Infrastructure.CompanyConfiguration;

/// <summary>Warehouse values in their stored (normalised) form. The code and the branch association are immutable once created.</summary>
public sealed record WarehouseData(
    string Code, string? NameAr, string? NameEn,
    string? AddressLine1, string? AddressLine2, string? City, string? Region, string? PostalCode, string? Phone, string? Email, string? Website);

/// <summary>Raw client input. <see cref="BranchId"/> is required on creation; on update it must be absent or equal to the stored one.</summary>
public sealed record WarehouseInput(
    string? Code, Guid? BranchId, string? NameAr, string? NameEn,
    string? AddressLine1, string? AddressLine2, string? City, string? Region, string? PostalCode, string? Phone, string? Email, string? Website);

/// <summary>
/// A warehouse as stored. <see cref="ActivationEpoch"/> advances on every activation-state change; warehouse-scoped access assignments are
/// effective only while the warehouse is active AND the epoch they recorded equals this value.
/// </summary>
public sealed record WarehouseView(
    Guid Id, Guid BranchId, WarehouseData Data, bool IsActive, long ActivationEpoch, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

/// <summary>The warehouse limit of the edition and the number of warehouses ever created (a new one is possible only while Created &lt; Limit).</summary>
public sealed record WarehouseLimits(int Limit, long EverCreated);

public static partial class ConfigurationValidation
{
    /// <summary>
    /// Validates a warehouse with the branch rules (code shape, at least one name, optional address/contact) plus the branch association:
    /// required on creation, immutable afterwards (<paramref name="existingBranchId"/> is set on update).
    /// </summary>
    public static bool TryNormalizeWarehouse(WarehouseInput? input, string? existingCode, Guid? existingBranchId, out WarehouseData data, out Guid branchId, out List<FieldError> errors)
    {
        data = default!;
        branchId = existingBranchId ?? Guid.Empty;
        if (input is null) { errors = [new("body", "required")]; return false; }
        TryNormalizeBranch(new BranchInput(input.Code, input.NameAr, input.NameEn, input.AddressLine1, input.AddressLine2, input.City, input.Region,
            input.PostalCode, input.Phone, input.Email, input.Website), existingCode, out var b, out errors);
        if (existingBranchId is null)
        {
            if (input.BranchId is null || input.BranchId == Guid.Empty) errors.Add(new("branchId", "required"));
            else branchId = input.BranchId.Value;
        }
        else if (input.BranchId is { } supplied && supplied != existingBranchId) errors.Add(new("branchId", "immutable"));
        if (errors.Count > 0) return false;
        data = new WarehouseData(b.Code, b.NameAr, b.NameEn, b.AddressLine1, b.AddressLine2, b.City, b.Region, b.PostalCode, b.Phone, b.Email, b.Website);
        return true;
    }
}
