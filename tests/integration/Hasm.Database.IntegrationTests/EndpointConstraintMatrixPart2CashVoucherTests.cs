using Mode = Hasm.Database.IntegrationTests.EndpointConstraintMatrixPart2Tests.Mode;
using CV = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CashVoucher;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Constraint-violation matrix, part 2, batch 6: cash vouchers without posting (Phase 3.14 slice S2a) on the cash fixture: an idempotent create, a stale-revision race of an edit and the three transitions, each sent in parallel and once
/// more. The rule, the engine (<see cref="ConstraintRaces"/>) and the completeness list are <see cref="EndpointConstraintMatrixPart2Tests"/>'s. The caller holds the voucher permissions through a test role (they are in no preset role).
/// Disposable cluster only.
/// </summary>
public class EndpointConstraintMatrixPart2CashVoucherTests : CashVoucherTestBase
{
    private readonly ConstraintRaces races = new(6);

    private Task<ApiResponse?> Race(string what, string[] expected, HttpMethod method, string path, object? body, string cookie, string? ifMatch = null, Mode mode = Mode.One) =>
        races.Run(what, expected, method, path, () => Send(method, path, body, cookie, ifMatch: ifMatch), mode);

    [PostgresFact]
    public async Task Cash_vouchers_end_in_a_mapped_4xx_never_a_5xx()
    {
        await StartV();
        var main = await MainBranch(); var box = await Box("cvv", main, "1101"); var sales = await Gl("4101", 4);
        var cookie = await AsMember(Clerk);
        // one key in parallel: every answer is a 2xx naming the SAME voucher (201 for the one that wrote it, 200 replayed for the others)
        var created = races.Won(await Race("voucher create", ["2xx"], HttpMethod.Post, V, Body("receipt", box, "matrix-key-0001", Line(sales, 10m)), cookie, mode: Mode.Same));
        var id = ConstraintRaces.IdOf(created);
        Assert.Equal(1L, await Rows(CV.Table));
        var updated = races.Won(await Race("voucher update", ["412:revision_conflict"], HttpMethod.Put, $"{V}/{id}", new { voucherDate = "2026-03-11", lines = new[] { Line(sales, 12m) } }, cookie, ConstraintRaces.EtagOf(created)));
        var submitted = races.Won(await Race("voucher submit", ["412:revision_conflict", "409:cash_voucher_not_draft"], HttpMethod.Post, $"{V}/{id}/submit", new { }, cookie, ConstraintRaces.EtagOf(updated)));
        var withdrawn = races.Won(await Race("voucher withdraw", ["412:revision_conflict", "409:cash_voucher_not_submitted"], HttpMethod.Post, $"{V}/{id}/withdraw", new { }, cookie, ConstraintRaces.EtagOf(submitted)));
        await Race("voucher cancel", ["412:revision_conflict", "409:cash_voucher_not_cancellable"], HttpMethod.Post, $"{V}/{id}/cancel", new { reason = "matrix" }, cookie, ConstraintRaces.EtagOf(withdrawn));
        Assert.Equal((5L, (short)5), (await DbRevision(id), await DbStatus(id)));
        races.AssertNoFindings();
        races.AssertRan(EndpointConstraintMatrixPart2Tests.Shares[nameof(Cash_vouchers_end_in_a_mapped_4xx_never_a_5xx)]);
    }
}
