using System.Reflection;
using Hasm.Api.Auth;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

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

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
