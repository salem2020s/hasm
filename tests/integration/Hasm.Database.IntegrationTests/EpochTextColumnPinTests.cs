using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using CO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostOperation;
using CE = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostEntry;
using CL = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostLayer;
using CC = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostConsumption;
using CPO = Hasm.Infrastructure.CompanyDatabase.CompanySchema.CostPosition;
using EMB = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochMember;
using ELN = Hasm.Infrastructure.CompanyDatabase.CompanySchema.EpochEntryLineage;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// T2 design section 12, test 4c (schema pinning, finding P2): every column of the four cost ledger tables of company migration 0014 AND of the three other tables the epoch text reads (the cost position table: P group; the epoch member table: O lines; the entry lineage table: E lines; review of PR 238, L1) is classified either SIGNED (it appears in the epoch text) or EXCLUDED with a
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
        // the P line renders the item, the warehouse, the last sequence, the quantity, the value and (from the layers) the number of open layers
        [CPO.Table] = ([CPO.ItemId, CPO.WarehouseId, CPO.LastSeq, CPO.Quantity, CPO.Value],
            new() { [CPO.Id] = "random uuid", [CPO.Method] = "the valuation method of the position, set when the position is created; no replay writes it and the entries follow it",
                [CPO.LastEffectiveDate] = "derived from the last entry, whose own effective date is an E line", [CPO.CreatedAtUtc] = "a time", [CPO.UpdatedAtUtc] = "a time",
                [CPO.LastAverageQuantity] = "the last quantity held while the position had stock, written by the 0014 guard from the entries (whose running quantities are E lines)",
                [CPO.LastAverageValue] = "the last value held while the position had stock, written by the 0014 guard from the entries (whose running values are E lines)" }),
        // the O line names the member's operation, its role (the order of the epoch) and the original operation it replaces; the execution selects the rows
        [EMB.Table] = ([EMB.OperationId, EMB.ExecutionId, EMB.Role, EMB.OriginalOperationId], new()),
        // the E line of a rebook names the original entry; the rebook entry is the line itself; the storno entry is the entry that reverses the original (the E line's own reversal reference)
        [ELN.Table] = ([ELN.OriginalEntryId, ELN.RebookEntryId, ELN.ExecutionId], new() { [ELN.StornoEntryId] = "the storno entry is the entry that reverses the original: its E line carries that reference" }),
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
    public async Task Every_column_of_the_seven_tables_the_epoch_text_reads_is_signed_or_excluded_with_a_reason()
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
