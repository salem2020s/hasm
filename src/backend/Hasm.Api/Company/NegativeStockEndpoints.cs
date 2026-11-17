using System.Globalization;
using Hasm.Api.Access;
using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Costing;
using Hasm.Infrastructure.Inventory;
using Microsoft.AspNetCore.Http.Metadata;

namespace Hasm.Api.Company;

public static class NegativeStockSetup
{
    public static IServiceCollection AddHasmNegativeStock(this IServiceCollection services)
    {
        services.AddSingleton(sp => new NegativeStockService(sp.GetRequiredService<CompanyDatabaseResolver>(), sp.GetRequiredService<CompanyAuditedTransactions>(), sp.GetRequiredService<InventoryCostingService>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new NegativeStockManagementService(sp.GetRequiredService<AccessService>(), sp.GetRequiredService<StockDocumentService>(), sp.GetRequiredService<NegativeStockService>()));
        return services;
    }
}

/// <summary>
/// The negative-stock exception (Phase 3.8B-3). /api/v1/company/negative-stock: the versioned company enablement (<c>If-Match</c> = version number), the per-item eligibility flags, the obligations and the close report;
/// /api/v1/company/stock-documents/{id}/negative-exception: the exception plan of an issue document and its approvals (<c>If-Match</c> = document revision). The company comes only from the server-side session, every call
/// is authorized in <see cref="NegativeStockManagementService"/>, responses are <c>no-store</c>. Posting itself stays the 3.7B endpoint. Web and Windows Desktop use exactly these endpoints.
/// </summary>
public static class NegativeStockEndpoints
{
    private sealed class BodyLimit : IRequestSizeLimitMetadata { public long? MaxRequestBodySize => 64 * 1024; }

    public sealed record SettingsBody(bool? Enabled, bool? RequireSecondApprover, string? Note);
    public sealed record FlagBody(bool? Eligible, string? Note);
    public sealed record ManualBody(int? LineNo, decimal? Amount, string? Justification);
    public sealed record ApprovalBody(string? Reason, List<ManualBody>? ManualCosts);

    public static void MapNegativeStockEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/company/negative-stock").RequireAuthorization();
        g.MapGet("/settings", GetSettings);
        g.MapPut("/settings", PutSettings).WithMetadata(new BodyLimit());
        g.MapGet("/items", Items);
        g.MapPut("/items/{itemId:guid}", PutItem).WithMetadata(new BodyLimit());
        g.MapGet("/obligations", Obligations);
        g.MapGet("/close-report", CloseReport);
        var d = app.MapGroup("/api/v1/company/stock-documents/{id:guid}/negative-exception").RequireAuthorization();
        d.MapGet("", GetException);
        d.MapPost("/approvals", Approve).WithMetadata(new BodyLimit());
    }

    private static object SettingsJson(NegativeStockSettingsView s) => new { versionNo = s.VersionNo, enabled = s.Enabled, requireSecondApprover = s.RequireSecondApprover, note = s.Note, createdBy = s.CreatedBy, createdAtUtc = s.CreatedAtUtc };

    private static object FlagJson(NegativeItemFlagView f) => new
    {
        itemId = f.ItemId, code = f.Code, nameAr = f.NameAr, nameEn = f.NameEn, eligible = f.Eligible, effectiveValuation = f.EffectiveValuation, tracked = f.Tracked, isActive = f.IsActive,
        hasOpenObligation = f.HasOpenObligation, note = f.Note, changedBy = f.ChangedBy, changedAtUtc = f.ChangedAtUtc,
    };

    private static object PlanJson(NegativeExceptionPlan p) => new
    {
        documentId = p.DocumentId, documentRevision = p.DocumentRevision, documentDate = p.DocumentDate, needed = p.Needed, accountingVersionId = p.AccountingVersionId, obligationAccountId = p.ObligationAccountId,
        varianceAccountId = p.VarianceAccountId, counterAccountId = p.CounterAccountId, problems = p.Problems, hash = p.Hash,
        lines = p.Lines.Select(l => new
        {
            lineNo = l.LineNo, itemId = l.ItemId, warehouseId = l.WarehouseId, quantity = l.Quantity, onHand = l.OnHand, covered = l.Covered, uncovered = l.Uncovered, source = l.Source, sourceEntryId = l.SourceEntryId,
            sourceQuantity = l.SourceQuantity, sourceValue = l.SourceValue, provisionalTotal = l.ProvisionalTotal, provisionalUnitCost = l.ProvisionalUnitCost, justification = l.Justification, problem = l.Problem,
        }),
    };

    private static object ExceptionJson(NegativeExceptionView v) => new
    {
        plan = PlanJson(v.Plan), enabled = v.Enabled, requireSecondApprover = v.RequireSecondApprover, approvalsRequired = v.ApprovalsRequired, validApprovals = v.ValidApprovals, posted = v.Posted,
        approvals = v.Approvals.Select(a => new { id = a.Id, approverId = a.ApproverId, approvedAtUtc = a.ApprovedAtUtc, reason = a.Reason, snapshotHash = a.SnapshotHash, current = a.Current }),
    };

    private static object ObligationJson(NegativeObligationView o) => new
    {
        id = o.Id, seq = o.Seq, itemId = o.ItemId, itemCode = o.ItemCode, itemNameAr = o.ItemNameAr, itemNameEn = o.ItemNameEn, warehouseId = o.WarehouseId, documentId = o.DocumentId, documentNumber = o.DocumentNumber,
        lineNo = o.LineNo, effectiveDate = o.EffectiveDate, originalQuantity = o.OriginalQuantity, provisionalTotal = o.ProvisionalTotal, openQuantity = o.OpenQuantity, openProvisional = o.OpenProvisional, source = o.Source,
        justification = o.Justification, status = o.Status, ageDays = o.AgeDays, journalEntryId = o.JournalEntryId, journalStatus = o.JournalStatus, obligationAccountId = o.ObligationAccountId, varianceAccountId = o.VarianceAccountId,
        settlements = o.Settlements.Select(s => new
        {
            id = s.Id, receiptPostingId = s.ReceiptPostingId, receiptDocumentId = s.ReceiptDocumentId, receiptNumber = s.ReceiptNumber, receiptLineNo = s.ReceiptLineNo, effectiveDate = s.EffectiveDate,
            matchedQuantity = s.MatchedQuantity, provisionalAmount = s.ProvisionalAmount, actualAmount = s.ActualAmount, difference = s.Difference, journalEntryId = s.JournalEntryId, reversesSettlementId = s.ReversesSettlementId, reversed = s.Reversed,
        }),
    };

    private static async Task<IResult> GetSettings(HttpContext http, AccessService access, NegativeStockManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.GetSettingsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, SettingsJson);
        var canConfigure = await access.IsAllowedAsync(actor.UserId, actor.CompanyId, PermissionCatalog.Codes.InventorySettingsConfigure, ScopeTarget.Company, http.RequestAborted);
        http.Response.Headers.ETag = $"\"{r.Value!.VersionNo}\"";
        return Results.Json(new { settings = SettingsJson(r.Value), canConfigure });
    }

    private static async Task<IResult> PutSettings(SettingsBody? body, HttpContext http, AccessService access, NegativeStockManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        return CompanyConfigurationEndpoints.Map(await svc.SaveSettingsAsync(actor!.UserId, actor.CompanyId, expected, new NegativeStockSettingsInput(body?.Enabled, body?.RequireSecondApprover, body?.Note), http.RequestAborted), SettingsJson, s => s.VersionNo);
    }

    private static async Task<IResult> Items(HttpContext http, AccessService access, NegativeStockManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.ListItemFlagsAsync(actor!.UserId, actor.CompanyId, http.RequestAborted);
        return r.IsOk ? Results.Json(new { items = r.Value!.Select(FlagJson) }) : CompanyConfigurationEndpoints.Map(r, _ => new { });
    }

    private static async Task<IResult> PutItem(Guid itemId, FlagBody? body, HttpContext http, AccessService access, NegativeStockManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        return CompanyConfigurationEndpoints.Map(await svc.SetItemFlagAsync(actor!.UserId, actor.CompanyId, itemId, new NegativeItemFlagInput(body?.Eligible, body?.Note), http.RequestAborted), FlagJson);
    }

    private static async Task<IResult> Obligations(HttpContext http, AccessService access, NegativeStockManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var status = http.Request.Query["status"].ToString();
        if (status is not ("" or "open" or "all")) return CompanyConfigurationEndpoints.Error(400, "invalid_status");
        var r = await svc.ListObligationsAsync(actor!.UserId, actor.CompanyId, status != "all", http.RequestAborted);
        return r.IsOk ? Results.Json(new { obligations = r.Value!.Select(ObligationJson) }) : CompanyConfigurationEndpoints.Map(r, _ => new { });
    }

    private static async Task<IResult> CloseReport(HttpContext http, AccessService access, NegativeStockManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!DateOnly.TryParseExact(http.Request.Query["periodEnd"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) return CompanyConfigurationEndpoints.Error(400, "invalid_period_end");
        var r = await svc.CloseReportAsync(actor!.UserId, actor.CompanyId, end, http.RequestAborted);
        return CompanyConfigurationEndpoints.Map(r, x => new
        {
            periodEnd = x.PeriodEnd, open = x.Open.Select(ObligationJson), expectedObligationBalance = x.ExpectedObligationBalance, ledgerObligationBalance = x.LedgerObligationBalance,
            settlementDifferences = x.SettlementDifferences, ledgerVariance = x.LedgerVariance, failedSettlementJournals = x.FailedSettlementJournals, reconciled = x.Reconciled,
            blocksFinalClose = x.Open.Count > 0 || !x.Reconciled,
        });
    }

    private static async Task<IResult> GetException(Guid id, HttpContext http, AccessService access, NegativeStockManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        var r = await svc.GetExceptionAsync(actor!.UserId, actor.CompanyId, id, http.RequestAborted);
        if (!r.IsOk) return CompanyConfigurationEndpoints.Map(r, ExceptionJson);
        var warehouses = r.Value!.Plan.Lines.Select(l => l.WarehouseId).Distinct().ToList();
        var canApprove = warehouses.Count > 0 && (await svc.ApprovableWarehousesAsync(actor.UserId, actor.CompanyId, warehouses, http.RequestAborted)).Count == warehouses.Count;
        return Results.Json(new { exception = ExceptionJson(r.Value), canApprove });
    }

    private static async Task<IResult> Approve(Guid id, ApprovalBody? body, HttpContext http, AccessService access, NegativeStockManagementService svc)
    {
        var (actor, failure) = await AccessEndpoints.ResolveActor(http, access);
        if (failure is not null) return failure;
        if (!CompanyConfigurationEndpoints.TryReadIfMatch(http, out var expected)) return CompanyConfigurationEndpoints.Error(400, "invalid_if_match");
        var input = new NegativeApprovalInput(body?.Reason, body?.ManualCosts?.Select(m => new NegativeManualCostInput(m.LineNo, m.Amount, m.Justification)).ToList());
        return CompanyConfigurationEndpoints.Map(await svc.ApproveAsync(actor!.UserId, actor.CompanyId, id, expected, input, http.RequestAborted), ExceptionJson);
    }
}
