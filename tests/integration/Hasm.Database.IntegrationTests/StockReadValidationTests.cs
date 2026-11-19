using System.Data;
using Hasm.Infrastructure.CompanyConfiguration;
using Hasm.Infrastructure.CompanyDatabase;
using Hasm.Infrastructure.Inventory;
using Npgsql;

namespace Hasm.Database.IntegrationTests;

public class StockReadValidationTests : StockTestBase
{
    private async Task<ConfigResult<StockDocumentService.ReadValidation>> Validate(StockDocumentInput input, bool readOnly,
        Action? command = null, Action? row = null, CancellationToken ct = default)
    {
        await using var conn = await Resolver.OpenAsync(Company.Id);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        if (readOnly) await using (var q = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await q.ExecuteNonQueryAsync();
        try
        {
            if (readOnly) return ConfigResult<StockDocumentService.ReadValidation>.Ok(await StockDocumentService.ValidateReadOnlyAsync(
                conn, tx, input, command ?? (() => { }), row ?? (() => { }), ct));
            if (!StockDocumentValidation.TryNormalize(input, null, out var header, out var lines, out var errors))
                return ConfigResult<StockDocumentService.ReadValidation>.Invalid(errors);
            var work = new CompanyAuditedWork(Company.Id, conn, tx, Audit, Clock);
            var branch = await StockDocumentService.ResolveWarehouses(work, header, ct);
            var resolved = await StockDocumentService.ResolveLines(work, lines, ct);
            return ConfigResult<StockDocumentService.ReadValidation>.Ok(new(header, branch.BranchId, branch.BranchCode, resolved));
        }
        catch (StockDocumentService.Abort e) { return e.To<StockDocumentService.ReadValidation>(); }
    }

    [PostgresFact]
    public async Task Read_only_resolution_matches_normal_lines_without_writes_or_unrelated_configuration_reads()
    {
        await Prepare();
        var input = Receipt(lines: [L(Apple, Box, 2, "first"), L(Pear, Kg, 1.234m, "second")]);
        var commands = 0; var rows = 0;
        var before = await Count(Company, CompanySchema.BusinessAudit.Table);
        var actual = Ok(await Validate(input, true, () => commands++, () => rows++));
        var normal = Ok(await Validate(input, false));
        Assert.Equal(normal.Header, actual.Header); Assert.Equal(normal.BranchId, actual.BranchId);
        Assert.Equal(normal.Lines, actual.Lines); Assert.Equal(new[] { 24m, 1.234m }, actual.Lines.Select(l => l.Base));
        Assert.Equal(new[] { "first", "second" }, actual.Lines.Select(l => l.Notes));
        Assert.Equal(5, commands); Assert.Equal(8, rows); // One warehouse/branch, two items, three units, one exact pair.
        Assert.Equal(before, await Count(Company, CompanySchema.BusinessAudit.Table)); Assert.Equal(0, await Count(Company, CompanySchema.StockDocument.Table));
    }

    [PostgresFact]
    public async Task Five_hundred_duplicate_lines_preserve_order_without_multiplying_reference_rows()
    {
        await Prepare(); var commands = 0; var rows = 0;
        var input = Receipt(lines: Enumerable.Range(1, 500).Select(n => L(Apple, Box, n, $"line-{n}")).ToArray());
        var result = Ok(await Validate(input, true, () => commands++, () => rows++));
        Assert.Equal(500, result.Lines.Count); Assert.Equal(6000m, result.Lines[^1].Base); Assert.Equal("line-500", result.Lines[^1].Notes);
        Assert.Equal(5, commands); Assert.Equal(6, rows);
        var tooMany = input with { Lines = input.Lines!.Append(L(Apple)).ToArray() };
        commands = 0; rows = 0;
        Invalid(await Validate(tooMany, true, () => commands++, () => rows++), "lines", "too_many");
        Assert.Equal(0, commands); Assert.Equal(0, rows);
    }

    [PostgresTheory]
    [InlineData("service")]
    [InlineData("tracked")]
    [InlineData("missing-unit")]
    [InlineData("undefined-unit")]
    [InlineData("entered-precision")]
    [InlineData("base-precision")]
    [InlineData("missing-item")]
    [InlineData("missing-warehouse")]
    public async Task Normal_and_read_only_adapters_reject_the_same_invalid_reference_or_conversion(string kind)
    {
        await Prepare();
        var line = kind switch
        {
            "service" => L(Service), "tracked" => L(Lot), "missing-unit" => L(Apple) with { UnitId = Guid.NewGuid() },
            "undefined-unit" => L(Apple, Kg), "entered-precision" => L(Apple, Pcs, 0.5m),
            "base-precision" => L(Apple, Box, 0.01m), "missing-item" => L(Apple) with { ItemId = Guid.NewGuid() }, _ => L(Apple)
        };
        var input = Receipt(lines: [line]);
        if (kind == "missing-warehouse") input = input with { TargetWarehouseId = Guid.NewGuid() };
        var normal = await Validate(input, false); var actual = await Validate(input, true);
        Assert.Equal(ConfigStatus.Invalid, normal.Status); Assert.Equal(normal.Status, actual.Status);
        Assert.Equal(normal.Code, actual.Code); Assert.NotEmpty(actual.Fields!); Assert.Equal(normal.Fields, actual.Fields);
        Assert.Null(actual.Value);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Posting_keeps_its_reference_locks_while_read_only_validation_takes_none(bool readOnly)
    {
        await Prepare(); var input = Receipt();
        Assert.True(StockDocumentValidation.TryNormalize(input, null, out var header, out var lines, out _));
        await using var conn = await Resolver.OpenAsync(Company.Id);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        Guid branch;
        if (readOnly)
        {
            await using (var q = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await q.ExecuteNonQueryAsync();
            branch = (await StockDocumentService.ValidateReadOnlyAsync(conn, tx, input, () => { }, () => { }, default)).BranchId;
        }
        else
        {
            var work = new CompanyAuditedWork(Company.Id, conn, tx, Audit, Clock);
            branch = (await StockDocumentService.ResolveWarehouses(work, header, default)).BranchId;
            await StockDocumentService.ResolveLines(work, lines, default);
        }
        foreach (var reference in new[] { (CompanySchema.Warehouse.Table, CompanySchema.Warehouse.Id, W1.Id),
                     (CompanySchema.Branch.Table, CompanySchema.Branch.Id, branch), (CompanySchema.Item.Table, CompanySchema.Item.Id, Apple.Id) })
        {
            await using var peer = await Resolver.OpenAsync(Company.Id); await using var peerTx = await peer.BeginTransactionAsync();
            await using var probe = new NpgsqlCommand($"SELECT {reference.Item2} FROM {reference.Table} WHERE {reference.Item2}=@id FOR UPDATE NOWAIT", peer, peerTx);
            probe.Parameters.AddWithValue("id", reference.Item3);
            if (readOnly) Assert.Equal(reference.Item3, (Guid)(await probe.ExecuteScalarAsync())!);
            else Assert.Equal("55P03", (await Assert.ThrowsAsync<PostgresException>(() => probe.ExecuteScalarAsync())).SqlState);
        }
    }

    [PostgresTheory]
    [InlineData("commands")]
    [InlineData("rows")]
    [InlineData("cancel")]
    public async Task Bounded_adapter_propagates_resource_exhaustion_and_cancellation(string kind)
    {
        await Prepare(); using var cancellation = new CancellationTokenSource();
        var commands = new ReadCommandBudget(kind == "commands" ? 4 : 10, default);
        var rows = new ReadCommandBudget(kind == "rows" ? 3 : 10, default);
        if (kind == "cancel") cancellation.Cancel();
        if (kind == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Validate(Receipt(), true, commands.Reserve, rows.Reserve, cancellation.Token));
            Assert.Equal(0, commands.Used); Assert.Equal(0, rows.Used);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ReadCommandBudgetException>(() => Validate(Receipt(), true, commands.Reserve, rows.Reserve));
            Assert.Equal(ReadBudgetFailure.Exhausted, error.Failure);
            Assert.Equal(kind == "commands" ? 4 : 3, kind == "commands" ? commands.Used : rows.Used);
        }
    }
}
