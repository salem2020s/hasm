using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostLayer;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostConsumption;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// T2 design section 12, test 4c (schema pinning, finding P2): every column of the four cost ledger tables of company migration 0014 is classified either SIGNED (it appears in the epoch text) or EXCLUDED with a
/// reason (the table of 4.4). A column added or renamed by a later migration fails this test until it is classified, signed (with a new message version) or excluded with a reason. Not an application flag: a pure comparison
/// of the live catalog with the classification.
/// </summary>
public class EpochTextColumnPinTests : CostingTestBase
{
    private static readonly Dictionary<string, (string[] Signed, Dictionary<string, string> Excluded)> Classification = new()
    {
        [CO.Table] = ([CO.Kind, CO.EffectiveDate, CO.ReversesOperationId, CO.SourceType, CO.SourceId, CO.Key, CO.CreatedBy],
            new() { [CO.Id] = "random uuid", [CO.RequestHash] = "technical idempotency digest of the request the signed key identifies", [CO.CreatedAtUtc] = "a time",
                [CO.ReversedByOperationId] = "NULL for a new operation; set only by a later reversal, which X6 refuses for epoch members" }),
        [CE.Table] = ([CE.ItemId, CE.WarehouseId, CE.Seq, CE.Kind, CE.Direction, CE.EffectiveDate, CE.Quantity, CE.Amount, CE.QuantityAfter, CE.ValueAfter, CE.ReversesEntryId],
            new() { [CE.Id] = "identity", [CE.OperationId] = "bound by role and origin", [CE.PositionId] = "determined by item and warehouse", [CE.CreatedAtUtc] = "a time" }),
        [CL.Table] = ([CL.ItemId, CL.WarehouseId, CL.Seq, CL.EntryId, CL.OriginalQuantity, CL.OriginalAmount, CL.SourceLayerId, CL.RemainingQuantity, CL.RemainingAmount],
            new() { [CL.Id] = "random uuid", [CL.PositionId] = "determined by item and warehouse", [CL.CreatedAtUtc] = "a time" }),
        [CC.Table] = ([CC.EntryId, CC.LayerId, CC.Quantity, CC.Amount, CC.Restores], new() { [CC.Id] = "identity" }),
    };

    /// <summary>The columns of a table that the classification does not name, and the classified names the table does not have.</summary>
    private static (string[] Unclassified, string[] Missing) Compare(string table, IReadOnlyCollection<string> actual)
    {
        var (signed, excluded) = Classification[table];
        var named = signed.Concat(excluded.Keys).ToList();
        Assert.Equal(named.Count, named.Distinct().Count());                                         // no column is both signed and excluded, none twice
        Assert.All(excluded.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));       // an excluded column always has a reason
        return ([.. actual.Except(named).Order()], [.. named.Except(actual).Order()]);
    }

    [PostgresFact]
    public async Task Every_column_of_the_four_cost_ledger_tables_is_signed_or_excluded_with_a_reason()
    {
        await PrepareCosting();
        foreach (var table in Classification.Keys)
        {
            await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT column_name FROM information_schema.columns WHERE table_name = @t AND table_schema = current_schema()", conn);
            cmd.Parameters.AddWithValue("t", table);
            var actual = new List<string>();
            await using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) actual.Add(r.GetString(0));
            Assert.NotEmpty(actual);
            var (unclassified, missing) = Compare(table, actual);
            Assert.True(unclassified.Length == 0 && missing.Length == 0, $"{table}: unclassified [{string.Join(",", unclassified)}], classified but absent [{string.Join(",", missing)}]");
        }
    }

    [Fact]
    public void A_new_or_a_renamed_column_is_detected_until_it_is_classified()
    {
        foreach (var table in Classification.Keys)
        {
            var real = Classification[table].Signed.Concat(Classification[table].Excluded.Keys).ToList();
            var ok = Compare(table, real); Assert.Empty(ok.Unclassified); Assert.Empty(ok.Missing);
            var added = Compare(table, [.. real, "c_new_column"]); Assert.Equal(["c_new_column"], added.Unclassified); Assert.Empty(added.Missing);                 // an added column
            var renamed = Compare(table, [.. real.Order().Skip(1), "c_renamed"]); Assert.Equal(["c_renamed"], renamed.Unclassified); Assert.Equal([real.Order().First()], renamed.Missing);   // a renamed column
        }
    }
}
