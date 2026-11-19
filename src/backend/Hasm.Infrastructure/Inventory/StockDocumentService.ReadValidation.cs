using Npgsql;

namespace Hasm.Infrastructure.Inventory;

public sealed partial class StockDocumentService
{
    internal sealed record ReadValidation(StockDocumentValidation.Header Header, Guid BranchId, string BranchCode,
        IReadOnlyList<Resolved> Lines);

    /// <summary>
    /// Internal validation data adapter for a caller-owned read-only snapshot. No authorization,
    /// source revision/approval, fiscal gate or posting authority: the eventual source reader must
    /// supply those. Shares the normal rules while retaining normal posting's FOR SHARE locks.
    /// </summary>
    internal static async Task<ReadValidation> ValidateReadOnlyAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        StockDocumentInput? input, Action reserveCommand, Action reserveRow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reserveCommand); ArgumentNullException.ThrowIfNull(reserveRow);
        ct.ThrowIfCancellationRequested();
        if (!StockDocumentValidation.TryNormalize(input, null, out var header, out var lines, out var errors))
            throw new Abort(Hasm.Infrastructure.CompanyConfiguration.ConfigStatus.Invalid, "validation_failed", errors);
        // TryNormalize bounds this to500 lines. Queries return <=2 warehouses,1 branch,
        // <=500 items, <=1000 referenced units and <=500 exact item/unit pairs.
        var branch = await ResolveWarehousesCore(conn, tx, header, true, reserveCommand, reserveRow, ct);
        var resolved = await ResolveLinesCore(conn, tx, lines, true, reserveCommand, reserveRow, ct);
        ct.ThrowIfCancellationRequested();
        return new(header, branch.BranchId, branch.BranchCode, resolved);
    }

    private static NpgsqlCommand ValidationCommand(string sql, NpgsqlConnection conn, NpgsqlTransaction tx, Action? reserveCommand)
    {
        reserveCommand?.Invoke();
        return new(sql, conn, tx);
    }
}
