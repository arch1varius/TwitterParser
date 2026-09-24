using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Parser.Core;
using Parser.Infrastructure;
using Xunit;

namespace Parser.Tests;

public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PARSER_BROWSER_TEST") != "1")
            Skip = "Set PARSER_BROWSER_TEST=1 after installing Playwright Chromium.";
    }
}

public sealed class BrowserTests
{
    [Fact]
    public async Task MissingSessionIsReportedBeforeLaunchingBrowser()
    {
        var source = new PlaywrightPostSource(Options.Create(new ParserOptions
        { StorageStatePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json") }));
        var now = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<SourceException>(async () =>
        {
            await foreach (var page in source.ReadAsync(new("alice", now.AddDays(-1), now), default)) { }
        });
        Assert.Equal("login_required", error.Code);
    }

    [BrowserFact]
    public async Task InstalledChromiumCanLaunchAndBlockVideoRequests()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/*", async route =>
        {
            if (route.Request.ResourceType == "media") { blocked.TrySetResult(); await route.AbortAsync(); }
            else await route.FulfillAsync(new() { ContentType = "text/html", Body = "<video autoplay muted src='/test.mp4'></video>" });
        });
        await page.GotoAsync("https://parser-test.invalid");
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
