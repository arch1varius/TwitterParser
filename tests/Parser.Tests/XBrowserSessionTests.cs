using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Parser.Core;
using Parser.Infrastructure;
using Xunit;

namespace Parser.Tests;

public sealed class XBrowserSessionTests
{
    [Fact]
    public void ConfigurationBindsSessionCredentials()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parser:AuthToken"] = "test-auth",
            ["Parser:CsrfToken"] = "test-csrf"
        }).Build();
        var settings = configuration.GetSection("Parser").Get<ParserOptions>()!;
        Assert.Equal("test-auth", settings.AuthToken);
        Assert.Equal("test-csrf", settings.CsrfToken);
    }

    [Theory]
    [InlineData("secret; ct0=other", null)]
    [InlineData("secret\r\n", null)]
    [InlineData("\"secret\"", null)]
    [InlineData("valid", "secret with spaces")]
    public void InvalidCookieIsRejectedWithoutExposingValue(string auth, string? csrf)
    {
        var error = Assert.Throws<SourceException>(() => XBrowserSession.CreateContextOptions(new()
        { AuthToken = auth, CsrfToken = csrf }));
        Assert.Equal("login_required", error.Code);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [BrowserFact]
    public async Task TokenOverridesUnreadableStoredSessionAndIsScopedToSecureXRequests()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"parser-session-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(statePath, "not valid stored JSON");
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(XBrowserSession.CreateContextOptions(new()
            { AuthToken = "test-auth", CsrfToken = "test-csrf", StorageStatePath = statePath }));
            var cookies = await context.CookiesAsync("https://x.com");
            var auth = Assert.Single(cookies, c => c.Name == "auth_token");
            Assert.Equal("test-auth", auth.Value);
            Assert.True(auth.HttpOnly);
            Assert.True(auth.Secure);
            Assert.Equal("test-csrf", Assert.Single(cookies, c => c.Name == "ct0").Value);
            Assert.Empty(await context.CookiesAsync("https://example.com"));
            Assert.Empty(await context.CookiesAsync("http://x.com"));

            // Fulfill locally: no real account and no request to X are needed.
            await context.RouteAsync("**/*", route => route.FulfillAsync(new()
            { ContentType = "text/html", Body = "<html><body>session test</body></html>" }));
            var page = await context.NewPageAsync();
            await page.GotoAsync("https://x.com");
            var visibleCookies = await page.EvaluateAsync<string>("document.cookie");
            Assert.Contains("ct0=test-csrf", visibleCookies);
            Assert.DoesNotContain("auth_token", visibleCookies);
            Assert.Equal("not valid stored JSON", await File.ReadAllTextAsync(statePath));
        }
        finally { File.Delete(statePath); }
    }

    [BrowserFact]
    public async Task AuthTokenAloneWorksWithoutAStorageFile()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"parser-missing-{Guid.NewGuid():N}.json");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(XBrowserSession.CreateContextOptions(new()
        { AuthToken = "test-auth", StorageStatePath = statePath }));
        var auth = Assert.Single(await context.CookiesAsync("https://x.com"));
        Assert.Equal("auth_token", auth.Name);
        Assert.Equal("test-auth", auth.Value);
        Assert.False(File.Exists(statePath));
    }

    [BrowserFact]
    public async Task StoredSessionStillLoadsWhenAuthTokenIsMissing()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"parser-session-{Guid.NewGuid():N}.json");
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using (var original = await browser.NewContextAsync())
            {
                await original.AddCookiesAsync([new Cookie
                { Name = "auth_token", Value = "stored-auth", Domain = ".x.com", Path = "/", Secure = true }]);
                await original.StorageStateAsync(new() { Path = statePath });
            }
            await using var restored = await browser.NewContextAsync(XBrowserSession.CreateContextOptions(new()
            { StorageStatePath = statePath, CsrfToken = "ignored-without-auth" }));
            var cookie = Assert.Single(await restored.CookiesAsync("https://x.com"));
            Assert.Equal("stored-auth", cookie.Value);
        }
        finally { if (File.Exists(statePath)) File.Delete(statePath); }
    }
}
