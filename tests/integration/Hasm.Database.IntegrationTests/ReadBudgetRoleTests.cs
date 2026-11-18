using System.Data;
using Hasm.Infrastructure.CompanyDatabase;

namespace Hasm.Database.IntegrationTests;

public class ReadBudgetRoleTests : CompanyRuntimeTestBase
{
    protected override bool RolesRequired => true;
    private sealed class RetryCredential(FileCompanyCredentialStore store) : ICompanyCredentialProvider
    {
        internal int Calls { get; private set; }
        public async Task<CompanyCredential?> GetAsync(Guid companyId, CancellationToken ct = default)
        {
            var credential = await store.GetAsync(companyId, ct);
            Calls++;
            return Calls == 1 && credential is not null ? credential with { Password = Guid.NewGuid().ToString("N") } : credential;
        }
    }

    [PostgresRoleFact]
    public Task Credential_retry_charges_both_registry_reads_and_all_runtime_privilege_queries() => VerifyRetry(false);
    [PostgresRoleFact]
    public Task Exhaustion_during_credential_retry_propagates_without_database_error_mapping() => VerifyRetry(true);

    private async Task VerifyRetry(bool limited)
    {
        await StartCompanies(); var company = await ReadyCompany();
        var granted = await new CompanyRuntimeProvisioner(Configured(useRoleAdmin: true), Registry).GrantAsync(company.Id);
        Assert.True(granted.LoginVerified);
        var provider = new RetryCredential(new FileCompanyCredentialStore(CredentialDirectory));
        var resolver = new CompanyDatabaseResolver(Configured(), Registry, provider, Clock);
        var budget = new ReadCommandBudget(limited ? 1 : CompanyRuntimePrivileges.Tables.Count + 8, default);
        if (limited)
        {
            var failure = await Assert.ThrowsAsync<ReadCommandBudgetException>(() => resolver.OpenAsync(company.Id, budget.Reserve, default));
            Assert.Equal(ReadBudgetFailure.Exhausted, failure.Failure);
            Assert.Equal(1, budget.Used); Assert.Equal(1, provider.Calls);
        }
        else
        {
            await using var connection = await resolver.OpenAsync(company.Id, budget.Reserve, default);
            Assert.Equal(ConnectionState.Open, connection.State); Assert.Equal(2, provider.Calls);
            Assert.Equal(CompanyRuntimePrivileges.Tables.Count + 8, budget.Used);
        }
    }
}
