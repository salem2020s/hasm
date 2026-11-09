using System.Reflection;
using Hasm.Api.Access;
using Hasm.Api.Auth;
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

builder.Services.AddHasmAuthentication(builder.Configuration);
builder.Services.AddHasmAccess(builder.Configuration);

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

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
