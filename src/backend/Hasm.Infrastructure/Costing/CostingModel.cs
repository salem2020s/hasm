namespace Hasm.Infrastructure.Costing;

/// <summary>Audit event types of the costing engine (1150-1159 of the business stream).</summary>
public static class CostingAuditEvents
{
    public const short Received = 1150;
    public const short Issued = 1151;
    public const short Transferred = 1152;
    public const short Reversed = 1153;
}

/// <summary>The business document an operation belongs to (for traceability only; the engine never reads or changes it).</summary>
public sealed record CostingSource(string? Type, Guid? Id);

/// <summary>Quantities are in the item's BASE unit; <see cref="Amount"/> is the exact total value of the receipt in the company base currency (at most the currency's minor units; zero is allowed for free goods).</summary>
public sealed record CostingReceiptLine(Guid ItemId, Guid WarehouseId, decimal Quantity, decimal Amount);
public sealed record CostingIssueLine(Guid ItemId, Guid WarehouseId, decimal Quantity);
public sealed record CostingTransferLine(Guid ItemId, decimal Quantity);

/// <summary><see cref="Key"/> is the idempotency key of the operation (1-100 characters): the same key with the same content returns the original result and changes nothing; the same key with other content is refused.</summary>
public sealed record CostingReceiptRequest(string Key, DateOnly EffectiveDate, CostingSource? Source, IReadOnlyList<CostingReceiptLine> Lines);
public sealed record CostingIssueRequest(string Key, DateOnly EffectiveDate, CostingSource? Source, IReadOnlyList<CostingIssueLine> Lines);
public sealed record CostingTransferRequest(string Key, DateOnly EffectiveDate, CostingSource? Source, Guid FromWarehouseId, Guid ToWarehouseId, IReadOnlyList<CostingTransferLine> Lines);
public sealed record CostingReversalRequest(string Key, string ReversesKey, DateOnly EffectiveDate, CostingSource? Source);

public sealed record CostConsumptionView(Guid LayerId, decimal Quantity, decimal Amount, bool Restores);

/// <summary>One entry of the costing ledger: what moved through one item in one warehouse, at which exact value, and the quantity and value that remain after it. UnitCost is derived and for display only.</summary>
public sealed record CostLineResult(long EntryId, Guid ItemId, Guid WarehouseId, CostEntryKind Kind, int Direction, decimal Quantity, decimal Amount, decimal UnitCost, decimal QuantityAfter, decimal ValueAfter,
    IReadOnlyList<CostConsumptionView> Consumptions);

/// <summary>What the posting slice needs for the accounting side: the net change of inventory value (and quantity) per item and warehouse. A transfer nets to zero in total; a receipt is a positive change, an issue a negative one.</summary>
public sealed record InventoryValueLine(Guid ItemId, Guid WarehouseId, decimal QuantityChange, decimal ValueChange);

/// <summary>The valuation output of one operation. <see cref="Replayed"/> is true when the key had been applied before and nothing was changed.</summary>
public sealed record CostingResult(Guid OperationId, string Key, CostOperationKind Kind, bool Replayed, DateOnly EffectiveDate, IReadOnlyList<CostLineResult> Lines)
{
    /// <summary>The signed totals per item and warehouse, deterministic order (warehouse, item).</summary>
    public IReadOnlyList<InventoryValueLine> ToValueLines() =>
        [.. Lines.GroupBy(l => (l.WarehouseId, l.ItemId)).OrderBy(g => g.Key.WarehouseId).ThenBy(g => g.Key.ItemId)
            .Select(g => new InventoryValueLine(g.Key.ItemId, g.Key.WarehouseId, g.Sum(l => l.Direction * l.Quantity), g.Sum(l => l.Direction * l.Amount)))];

    /// <summary>The total change of inventory value of the operation (zero for a transfer: no artificial profit or loss).</summary>
    public decimal NetValueChange => Lines.Sum(l => l.Direction * l.Amount);
}

public sealed record CostLayerView(Guid Id, long Seq, decimal OriginalQuantity, decimal OriginalAmount, decimal RemainingQuantity, decimal RemainingAmount, Guid? SourceLayerId);

/// <summary>The cost position of one item in one warehouse. AverageUnitCost is derived (value / quantity), display only.</summary>
public sealed record CostPositionView(Guid Id, Guid ItemId, Guid WarehouseId, CostMethod Method, decimal Quantity, decimal Value, decimal AverageUnitCost, long LastEntrySeq, IReadOnlyList<CostLayerView> OpenLayers);

/// <summary>One broken invariant found by the reconciliation (an empty list means the costing ledger is consistent at item, warehouse and company level).</summary>
public sealed record CostDiscrepancy(string Check, string Detail);
