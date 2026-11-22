using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Cash;
using Mode = Hasm.Database.IntegrationTests.EndpointConstraintMatrixPart2Tests.Mode;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Constraint-violation matrix, part 2, batch 5: the cash and bank account master (Phase 3.14 slice S1) on the cash fixture: a duplicate code, a stale-revision race and the two transitions, each sent in parallel and once more. The rule, the
/// engine (<see cref="ConstraintRaces"/>) and the completeness list are <see cref="EndpointConstraintMatrixPart2Tests"/>'s. The caller is a member holding the cash permissions through a test role (the preset grant is a later system migration).
/// Disposable cluster only.
/// </summary>
public class EndpointConstraintMatrixPart2CashTests : CashAccountTestBase
{
    private readonly ConstraintRaces races = new(6);

    private Task<ApiResponse?> Race(string what, string[] expected, HttpMethod method, string path, object? body, string cookie, string? ifMatch = null, Mode mode = Mode.One) =>
        races.Run(what, expected, method, path, () => Send(method, path, body, cookie, ifMatch: ifMatch), mode);

    [PostgresFact]
    public async Task Cash_and_bank_accounts_end_in_a_mapped_4xx_never_a_5xx()
    {
        await Start();
        var main = await MainBranch(); var g1 = await Gl("1101"); var g2 = await Gl("1102");
        var cookie = await Manager();
        var created = races.Won(await Race("cash create", ["409:cash_account_code_taken", "409:gl_account_already_bound"], HttpMethod.Post, Base, Cashbox("cvc", main, g1), cookie));
        var id = ConstraintRaces.IdOf(created);
        object Body(string en) => new { kind = "cashbox", code = "cvc", nameAr = "الصندوق", nameEn = en, branchId = main, accountId = g1 };
        var updated = races.Won(await Race("cash update", ["412:revision_conflict"], HttpMethod.Put, $"{Base}/{id}", Body("Renamed"), cookie, ConstraintRaces.EtagOf(created)));
        var off = races.Won(await Race("cash deactivate", ["412:revision_conflict", "409:cash_account_already_inactive"], HttpMethod.Post, $"{Base}/{id}/deactivate", new { }, cookie, ConstraintRaces.EtagOf(updated)));
        await Race("cash activate", ["412:revision_conflict", "409:cash_account_already_active"], HttpMethod.Post, $"{Base}/{id}/activate", new { }, cookie, ConstraintRaces.EtagOf(off));
        // two DIFFERENT codes racing for one ledger account: exactly one wins, the other is the mapped binding conflict
        var other = Guid.NewGuid();
        var contenders = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => Send(HttpMethod.Post, Base, Cashbox("race" + i, main, g2), cookie))));
        Assert.Equal(1, contenders.Count(r => (int)r.Status is >= 200 and < 300));
        Assert.All(contenders.Where(r => (int)r.Status >= 300), r => Assert.Equal("gl_account_already_bound", r.Error));
        _ = other;
        await Race("cash rewrap", ["2xx"], HttpMethod.Post, $"{Base}/maintenance/rewrap", new { }, cookie, mode: Mode.Same);   // idempotent: every answer is a 200, nothing to move here
        races.AssertNoFindings();
        races.AssertRan(EndpointConstraintMatrixPart2Tests.Shares[nameof(Cash_and_bank_accounts_end_in_a_mapped_4xx_never_a_5xx)]);
    }
}
