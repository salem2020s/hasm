using Hasm.Infrastructure.Access;
using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Identity;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Au = Hasm.Infrastructure.SystemDatabase.SystemSchema.AccessAudit;
using Co = Hasm.Infrastructure.SystemDatabase.SystemSchema.Company;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>Migration 0005: repeatable, upgrade-compatible, history-preserving, not publicly accessible.</summary>
public class AuditMigrationTests : AuditTestBase
{
    [PostgresFact]
    public async Task Migration_0005_is_applied_once_and_repeating_is_a_noop()
    {
        var first = await new SystemDatabaseInitializer(Options).InitializeAsync();
        Assert.Contains(5, first.AppliedMigrations);
        var checksum = await Scalar<string>($"SELECT checksum FROM {SystemSchema.MigrationJournalTable} WHERE version = 5");

        var second = await new SystemDatabaseInitializer(Options).InitializeAsync();

        Assert.Empty(second.AppliedMigrations);
        Assert.Equal(checksum, await Scalar<string>($"SELECT checksum FROM {SystemSchema.MigrationJournalTable} WHERE version = 5"));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM {SystemSchema.MigrationJournalTable} WHERE version = 5"));
        Assert.Equal(0L, await EventCount());
    }

    [PostgresFact]
    public async Task Upgrading_from_version_4_preserves_audit_history_and_starts_the_chain_at_the_first_new_event()
    {
        await StartAccess();
        var company = await NewCompany();
        // Simulate an installation that only had migrations 1-4 and audit history written before any chain existed (disposable test database only).
        await Exec($"INSERT INTO {Au.Table} ({Au.OccurredAtUtc},{Au.CompanyId},{Au.EventCode},{Au.Detail}) VALUES (now(), @c, 1, 'old history'), (now(), @c, 2, NULL)", ("c", company));
        await Exec($"DROP TABLE {Ev.Table}; DROP FUNCTION {Ev.GuardFunction}(); DELETE FROM {SystemSchema.MigrationJournalTable} WHERE version = 5");
        var legacyMax = await Scalar<long>($"SELECT max({Au.Id}) FROM {Au.Table}");
        var snapshot = $"SELECT md5(string_agg({Au.Id}::text || {Au.OccurredAtUtc}::text || {Au.EventCode}::text || coalesce({Au.Detail}, ''), '|' ORDER BY {Au.Id})) FROM {Au.Table} WHERE {Au.Id} <= {legacyMax}";
        var before = await Scalar<string>(snapshot);

        var upgrade = await new SystemDatabaseInitializer(Options).InitializeAsync();

        Assert.Equal([5], upgrade.AppliedMigrations);
        Assert.Equal(before, await Scalar<string>(snapshot));                                               // history neither rewritten nor deleted
        var stream = AuditStream.ForCompany(company);
        var empty = await Verify(stream);
        Assert.Equal(AuditVerdict.Incomplete, empty.Verdict);                                              // an empty chain next to unauthenticated history is never "Verified"
        Assert.Contains("no_authenticated_history", Codes(empty, FindingSeverity.Incomplete));
        Assert.Equal(0, empty.Events);
        Assert.Equal(2, empty.LegacyUnprotectedRows);                                                       // reported honestly as unauthenticated history
        Assert.Contains("legacy_unprotected_rows", Codes(empty, FindingSeverity.Note));

        await NewAdmin(company);                                                                            // first chained events

        var after = await Verify(stream);
        AssertVerified(after, 2);
        Assert.Equal(2, after.LegacyUnprotectedRows);                                                       // old rows stay unauthenticated
        Assert.Equal(before, await Scalar<string>(snapshot));                                               // and unmodified by the new chain
        Assert.Equal(AuditCanonical.GenesisHash(stream.Key), (await Events(stream))[0].PrevHash);
    }

    [PostgresFact]
    public async Task Physical_schema_matches_the_logical_map_and_the_table_is_not_accessible_to_public()
    {
        await new SystemDatabaseInitializer(Options).InitializeAsync();
        var columns = new[] { Ev.StreamKey, Ev.Seq, Ev.EventId, Ev.CompanyId, Ev.OccurredAtUtc, Ev.EventType, Ev.ActorUserId, Ev.TargetUserId, Ev.SubjectId,
            Ev.LegacyRef, Ev.Detail, Ev.FormatVersion, Ev.KeyId, Ev.PrevHash, Ev.Hash, Ev.Mac };
        foreach (var c in columns)
            Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_name=@t AND column_name=@c", ("t", Ev.Table), ("c", c)));
        Assert.Equal((long)columns.Length, await Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_name=@t", ("t", Ev.Table)));
        foreach (var k in new[] { Ev.PrimaryKey, Ev.UniqueEventId, Ev.ForeignKeyCompany, Ev.ForeignKeyActor, Ev.ForeignKeyTarget, Ev.CheckStream, Ev.CheckLengths, Ev.CheckDetail, Ev.CheckFields })
            Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_constraint WHERE conname=@n", ("n", k)));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_trigger WHERE tgname=@n", ("n", Ev.GuardTrigger)));
        var acl = await Scalar<string>("SELECT coalesce(relacl::text, '') FROM pg_class WHERE relname=@t", ("t", Ev.Table));
        Assert.DoesNotMatch(@"(^\{|,)=", acl);                                                              // no PUBLIC entry
        Assert.All(columns.Concat([Ev.Table]), n => Assert.Matches("^[tc]_[0-9a-f]{8}$", n));                // opaque physical naming preserved
    }

    [PostgresFact]
    public async Task Database_constraints_reject_inconsistent_rows_and_ordinary_modification()
    {
        var (company, _, stream) = await Scenario(1);
        var other = await NewCompany();

        // Append-only: UPDATE and DELETE are refused for the table owner, not only for other roles.
        var update = await Assert.ThrowsAsync<PostgresException>(() => Exec($"UPDATE {Ev.Table} SET {Ev.Detail} = 'x'"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => Exec($"DELETE FROM {Ev.Table}"));
        Assert.Contains("append-only", update.MessageText);
        Assert.Contains("append-only", delete.MessageText);
        Assert.Equal(3L, await EventCount(stream));

        // A row cannot claim a stream that does not match its company.
        var keys = AuditKeys.GetActive();
        var wrongStream = AuditCanonical.Seal(new AuditRecord(stream.Key, 99, Guid.NewGuid(), 1, FutureEvent, other, null, null, null, null, null, 1, keys.Id, new byte[32], [], []), keys);
        await Assert.ThrowsAsync<PostgresException>(() => InsertRaw(wrongStream));
        var badLength = AuditCanonical.Seal(new AuditRecord(stream.Key, 98, Guid.NewGuid(), 1, FutureEvent, company, null, null, null, null, null, 1, keys.Id, new byte[32], [], []), keys);
        await Assert.ThrowsAsync<PostgresException>(() => InsertRaw(badLength with { Mac = new byte[31] }));
        Assert.Equal(3L, await EventCount(stream));
    }
}

/// <summary>A valid authenticated chain, independent streams, stable ordering, company isolation.</summary>
public class AuditChainTests : AuditTestBase
{
    [PostgresFact]
    public async Task Access_operations_produce_a_valid_authenticated_chain_anchored_in_the_checkpoint_store()
    {
        var (company, admin, stream) = await Scenario(3);
        var member = await NewMember(company, admin);
        var role = Ok(await Access.CreateRoleAsync(admin.Id, company, "Auditors", [PermissionCatalog.Codes.AuditView]));
        var assignment = Ok(await Access.AssignRoleAsync(admin.Id, company, member.Id, role.Id, ScopeKind.Company, null));
        Ok(await Access.RevokeAssignmentAsync(admin.Id, company, assignment.Id));
        Ok(await Access.SetRolePermissionsAsync(admin.Id, company, role.Id, [PermissionCatalog.Codes.MembersView]));
        Ok(await Access.DeactivateRoleAsync(admin.Id, company, role.Id));
        Ok(await Access.RevokeMembershipAsync(admin.Id, company, member.Id));

        var events = await Events(stream);
        var report = await Verify(stream);

        AssertVerified(report, events.Count);
        Assert.Equal(events.Count, await Scalar<long>($"SELECT count(*) FROM {Au.Table} WHERE {Au.CompanyId} = @c", ("c", company)));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Seq));
        Assert.Equal(AuditCanonical.GenesisHash(stream.Key), events[0].PrevHash);
        for (var i = 1; i < events.Count; i++) Assert.Equal(events[i - 1].Hash, events[i].PrevHash);
        Assert.Equal(events.Count, events.Select(e => e.EventId).Distinct().Count());
        Assert.All(events, e => { Assert.Equal(company, e.CompanyId); Assert.Equal("k1", e.KeyId); Assert.Equal(1, e.FormatVersion); Assert.Equal(32, e.Mac.Length); });
        Assert.Equal(events.Count, report.LastCheckpointSeq);
        Assert.Equal(events.Count - 1, report.Checkpoints);                                                // anchored after every commit; the bootstrap transaction wrote two events and is anchored once, at its newest
        Assert.Equal(0, report.LegacyUnprotectedRows);
        // Event types and identities mirror the access-audit projection.
        Assert.Equal(
            await Scalar<string>($"SELECT string_agg({Au.EventCode}::text, ',' ORDER BY {Au.Id}) FROM {Au.Table} WHERE {Au.CompanyId} = @c", ("c", company)),
            string.Join(',', events.Select(e => e.EventType)));
    }

    [PostgresFact]
    public async Task Identity_events_form_an_independent_system_stream()
    {
        await StartAccess();
        var companyStream = AuditStream.ForCompany(await NewCompany());
        var root = (await Auth.BootstrapAdministratorAsync("root", "Root", Pw))!.User!;
        var alice = Assert.IsType<UserRecord>((await Auth.CreateUserAsync("alice", "Alice", Pw, false, default, root.Id)).User);
        Assert.Equal(AuthService.SetStateStatus.Changed, await Auth.SetUserStateAsync(root.Id, alice.Id, UserState.Disabled));
        Assert.Equal(AuthService.SetStateStatus.Changed, await Auth.SetUserStateAsync(root.Id, alice.Id, UserState.Active));
        await Store.UpdatePasswordHashAsync(alice.Id, Passwords.Hash("Another-Passphrase-77"), passwordChanged: true);
        await Store.UpdatePasswordHashAsync(alice.Id, Passwords.Hash("Another-Passphrase-77"), passwordChanged: false);   // transparent rehash: not an event

        var events = await Events(AuditStream.System);

        Assert.Equal(
            [AuditEventTypes.InitialAdministratorCreated, AuditEventTypes.UserCreated, AuditEventTypes.UserStateChanged, AuditEventTypes.UserStateChanged, AuditEventTypes.PasswordChanged],
            events.Select(e => e.EventType));
        Assert.All(events, e => Assert.Null(e.CompanyId));
        Assert.Null(events[0].ActorUserId); Assert.Equal(root.Id, events[0].TargetUserId);
        Assert.Equal(root.Id, events[1].ActorUserId); Assert.Equal(alice.Id, events[1].TargetUserId);
        Assert.Equal("state: Disabled", events[2].Detail);
        Assert.Equal(alice.Id, events[4].ActorUserId);
        AssertVerified(await Verify(AuditStream.System), 5);
        Assert.Equal(0, (await Verify(companyStream)).Events);                                              // company streams are independent
    }

    [PostgresFact]
    public async Task Ordering_and_identity_are_stable_when_every_event_has_the_same_timestamp()
    {
        var (_, _, stream) = await Scenario(5);                                                            // FakeClock never advances: all timestamps are equal
        var events = await Events(stream);

        Assert.Single(events.Select(e => e.OccurredMicros).Distinct());
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Seq));    // order comes from the sequence, not from time
        Assert.Equal(events.Select(e => e.Seq), (await Events(stream)).Select(e => e.Seq));                 // reading twice yields the same order
        AssertVerified(await Verify(stream), events.Count);
    }

    [PostgresFact]
    public async Task Company_streams_are_isolated_during_storage_and_verification()
    {
        await StartAccess();
        var a = await NewCompany(); var b = await NewCompany();
        var adminA = await NewAdmin(a); var adminB = await NewAdmin(b);
        Ok(await Access.CreateRoleAsync(adminA.Id, a, "Only A", [PermissionCatalog.Codes.MembersView]));
        var sa = AuditStream.ForCompany(a); var sb = AuditStream.ForCompany(b);

        Assert.All(await Events(sa), e => Assert.Equal(a, e.CompanyId));
        Assert.All(await Events(sb), e => Assert.Equal(b, e.CompanyId));
        Assert.Equal(3, (await Events(sa)).Count); Assert.Equal(2, (await Events(sb)).Count);
        Assert.All(await Events(sa), e => Assert.NotEqual(adminB.Id, e.TargetUserId));

        // Damage in company B is reported for B only; verifying A neither reads nor reports it.
        await Tamper($"UPDATE {Ev.Table} SET {Ev.Detail} = 'x' WHERE {Ev.StreamKey} = @s AND {Ev.Seq} = 1", ("s", sb.Key));
        AssertVerified(await Verify(sa), 3);
        Assert.Equal(AuditVerdict.Failed, (await Verify(sb)).Verdict);

        // An unknown company has an empty, trivially verified stream; nothing of other companies shows through.
        var unknown = await Verify(AuditStream.ForCompany(Guid.NewGuid()));
        Assert.Equal(0, unknown.Events);
        Assert.Equal(AuditVerdict.Verified, unknown.Verdict);
    }

    [PostgresFact]
    public async Task Denied_or_failed_operations_write_no_audit_event()
    {
        var (company, admin, stream) = await Scenario(1);
        var outsider = await NewPerson("outsider");
        var before = await EventCount();

        Fails(await Access.CreateRoleAsync(outsider.Id, company, "Nope", [PermissionCatalog.Codes.MembersView]), AccessStatus.Forbidden);
        Fails(await Access.GrantMembershipAsync(admin.Id, company, "no-such-user"), AccessStatus.NotFound);
        Fails(await Access.CreateRoleAsync(admin.Id, company, "Role 0", [PermissionCatalog.Codes.MembersView]), AccessStatus.Conflict);   // duplicate name

        Assert.Equal(before, await EventCount());
        AssertVerified(await Verify(stream), 3);
    }

    [PostgresFact]
    public async Task Audit_events_never_contain_passwords_hashes_or_session_tokens()
    {
        await StartAccess();
        var root = (await Auth.BootstrapAdministratorAsync("root", "Root", Pw))!.User!;
        await Auth.CreateUserAsync("bob", "Bob", "Secret-Passphrase-123", false, default, root.Id);
        var login = await LoginOk("bob", "Secret-Passphrase-123");
        var dump = await Scalar<string>($"SELECT coalesce(string_agg(t::text, '|'), '') FROM {Ev.Table} t");

        Assert.DoesNotContain("Secret-Passphrase-123", dump);
        Assert.DoesNotContain(Pw, dump);
        Assert.DoesNotContain(login.Token, dump);
        Assert.DoesNotContain("AQAAAA", dump);                                                              // PasswordHasher v3 prefix
    }
}
