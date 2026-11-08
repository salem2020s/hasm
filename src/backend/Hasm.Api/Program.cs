using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Allowed browser/desktop origins come from configuration (Cors:AllowedOrigins); none are hardcoded.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
        }
    }));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors();

app.MapGet("/api/v1/health", () => Results.Ok(new
{
    status = "healthy",
    service = "hasm-api",
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
}));

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
