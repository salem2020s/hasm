using System.Reflection;
using Hasm.Api.Access;
using Hasm.Api.Auth;
using Hasm.Api.Company;
using Hasm.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Forwarded client-address headers are trusted only from explicitly configured proxies (Hasm:ReverseProxy); invalid or unsafe
// settings stop the process at startup.
var reverseProxy = ReverseProxyOptions.FromConfiguration(builder.Configuration);

// Allowed browser/desktop origins come from configuration (Cors:AllowedOrigins); none are hardcoded.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            // Credentials are allowed only for the explicitly configured origins (Web cookie sessions).
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials().WithExposedHeaders("Retry-After");
        }
    }));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHasmAudit(builder.Configuration);
builder.Services.AddHasmAuthentication(builder.Configuration);
builder.Services.AddHasmAccess(builder.Configuration);
builder.Services.AddHasmCompanyRuntime(builder.Configuration);
builder.Services.AddHasmCompanyConfiguration(builder.Configuration);
builder.Services.AddHasmBranches();
builder.Services.AddHasmWarehouses();
builder.Services.AddHasmTaxes(builder.Configuration);
builder.Services.AddHasmPartyRoles();
builder.Services.AddHasmInventory();
builder.Services.AddHasmStockDocuments();
builder.Services.AddHasmInventoryAccounting();
builder.Services.AddHasmStockPosting();
builder.Services.AddHasmStockBalances();
builder.Services.AddHasmStocktakes();
builder.Services.AddHasmStockAdjustments();
builder.Services.AddHasmNegativeStock();
builder.Services.AddHasmFiscalCalendar();
builder.Services.AddHasmChartOfAccounts();
builder.Services.AddHasmLedger();
builder.Services.AddHasmImports(builder.Configuration);
builder.Services.AddHasmFinancialStatements();
builder.Services.AddHasmParties(builder.Configuration);
// Decimal amounts and rates may be sent as exact JSON strings ("100.1250"): the shared frontend never routes money through floating point.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString);

var app = builder.Build();

app.UseHasmReverseProxy(reverseProxy);
app.UseHasmExceptionHandling();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseHasmResponseHardening();
app.UseHasmCsrfProtection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/v1/health", () => Results.Ok(new
{
    status = "healthy",
    service = "hasm-api",
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
}));

app.MapAuthEndpoints();
app.MapAccessEndpoints();
app.MapCompanyConfigurationEndpoints();
app.MapBranchEndpoints();
app.MapWarehouseEndpoints();
app.MapTaxEndpoints();
app.MapPartyRoleEndpoints();
app.MapInventoryEndpoints();
app.MapStockDocumentEndpoints();
app.MapStockPostingEndpoints();
app.MapStockBalanceEndpoints();
app.MapStocktakeEndpoints();
app.MapStockAdjustmentEndpoints();
app.MapNegativeStockEndpoints();
app.MapInventoryAccountingEndpoints();
app.MapFiscalEndpoints();
app.MapAccountEndpoints();
app.MapNumberingEndpoints();
app.MapExchangeRateEndpoints();
app.MapCostCenterEndpoints();
app.MapApprovalPolicyEndpoints();
app.MapJournalEndpoints();
app.MapCostCenterPolicyEndpoints();
app.MapFiscalClosingEndpoints();
app.MapLedgerReportEndpoints();
app.MapFinancialStatementEndpoints();
app.MapImportEndpoints();
app.MapPartyEndpoints();

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
