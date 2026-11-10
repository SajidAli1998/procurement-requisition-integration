using System.Security.Cryptography;
using Procurement.TestLab;
using Scalar.AspNetCore;

if (args.Length > 0 && Cli.Commands.Contains(args[0])) return await Cli.RunAsync(args);

var builder = WebApplication.CreateBuilder(args.Where(a => a != "serve").ToArray());
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5110");
builder.Services.AddSingleton<Workspace>();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:3000", "http://127.0.0.1:3000").AllowAnyHeader().AllowAnyMethod()));
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

// Set TESTLAB_KEY to share the key with the web app's server; otherwise a random one is printed at startup.
var key = Environment.GetEnvironmentVariable("TESTLAB_KEY") is { Length: >= 16 } k ? k : Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();
app.MapOpenApi();
app.MapScalarApiReference();
Api.Map(app, key);
if (Environment.GetEnvironmentVariable("TESTLAB_KEY") is null)
    Console.WriteLine($"Lab key (send as {Api.KeyHeader}): {key}");
await app.RunAsync();
return 0;

namespace Procurement.TestLab
{
    /// <summary>Marker for WebApplicationFactory in the Test Lab's own API tests.</summary>
    public sealed class TestLabMarker;
}
