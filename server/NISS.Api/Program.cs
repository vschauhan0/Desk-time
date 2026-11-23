using NISS.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddNissConfiguration(args);
builder.AddNissLogging();
var migrateOnly = args.Contains("--migrate");
if (!migrateOnly) builder.ConfigureNissKestrel();   // fails fast at Build() when no certificate and no AllowInsecureHttp
builder.AddNissServices();

WebApplication app;
try
{
    app = builder.Build();
}
catch (InvalidOperationException ex)
{
    // Configuration problems (for example: no TLS certificate and AllowInsecureHttp=false) end here with a readable message.
    Console.Error.WriteLine($"NISS Monitor cannot start: {ex.Message}");
    return 2;
}

if (migrateOnly)
    return await app.MigrateAndExitAsync();

await app.InitializeAsync();
app.UseNissPipeline();
app.MapNissEndpoints();

var plainHttp = KestrelConfiguration.IsPlainHttp(app.Configuration, app.Environment.ContentRootPath);
if (plainHttp && !app.Environment.IsEnvironment("Testing"))
    app.Logger.LogWarning("Listening on plain HTTP (Server:AllowInsecureHttp). Do not use this outside local development.");

await app.RunAsync();
return 0;

/// <summary>Entry point marker so integration tests can host the API with <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
