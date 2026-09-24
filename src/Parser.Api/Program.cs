using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Parser.Api;
using Parser.Core;
using Parser.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true)
    .AddProjectEnvFile(builder.Environment.ContentRootPath)
    .AddEnvironmentVariables();
builder.Services.AddOptions<ParserOptions>().BindConfiguration("Parser")
    .Validate(o => o.MaxScrolls is > 0 and <= 10000 && o.ScrollDelayMs >= 500
        && o.MaxJobMinutes is > 0 and <= 1440 && o.MaxPhotoMegabytes is > 0 and <= 100
        && (o.RetentionDays is null or > 0), "Invalid parser limits.").ValidateOnStart();

if (args.Contains("--login-x", StringComparer.Ordinal))
{
    await PlaywrightPostSource.LoginAsync(builder.Configuration.GetSection("Parser").Get<ParserOptions>() ?? new());
    return;
}

builder.Services.AddDbContext<ParserDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Parser")));
builder.Services.AddScoped<IPostSource, PlaywrightPostSource>();
builder.Services.AddHttpClient<IPhotoStore, PhotoStore>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<PostImporter>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddHostedService<ParseWorker>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
var app = builder.Build();

if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Parser")))
    throw new InvalidOperationException("Set ConnectionStrings__Parser in the root .env or environment, or ConnectionStrings:Parser in appsettings.Local.json.");
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<ParserDbContext>().Database.MigrateAsync();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var key = app.Configuration["Api:Key"];
    var loopback = context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
    var allowLocal = app.Configuration.GetValue("Api:AllowAnonymousLoopback", true);
    if (string.IsNullOrWhiteSpace(key))
    {
        if (!loopback || !allowLocal)
        { context.Response.StatusCode = 401; return; }
    }
    else
    {
        var supplied = context.Request.Headers["X-Api-Key"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        { context.Response.StatusCode = 401; return; }
    }
    await next(context);
});
app.MapOpenApi();
app.MapParserApi();
await app.RunAsync();

public partial class Program { }
