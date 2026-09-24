using System.Text.Json;
using Microsoft.Playwright;
using Parser.Core;

namespace Parser.Infrastructure;

public static class XBrowserSession
{
    public static BrowserNewContextOptions CreateContextOptions(ParserOptions settings)
    {
        var options = new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1280, Height = 900 },
            Locale = "en-US",
            ServiceWorkers = ServiceWorkerPolicy.Block
        };
        if (string.IsNullOrWhiteSpace(settings.AuthToken))
        {
            if (!File.Exists(settings.AuthenticationPath))
                throw new SourceException("login_required",
                    "Выполните вход через --login-x или задайте Parser__AuthToken (cookie auth_token из X).");
            options.StorageStatePath = settings.AuthenticationPath;
            return options;
        }

        // Explicit credentials replace the entire stored session to avoid mixing accounts.
        var cookies = new List<object> { CreateCookie("auth_token", settings.AuthToken, httpOnly: true) };
        if (!string.IsNullOrWhiteSpace(settings.CsrfToken))
            cookies.Add(CreateCookie("ct0", settings.CsrfToken, httpOnly: false));
        options.StorageState = JsonSerializer.Serialize(new { cookies, origins = Array.Empty<object>() });
        return options;
    }

    private static object CreateCookie(string name, string value, bool httpOnly)
    {
        // Reject pasted Cookie headers and control characters without echoing secrets.
        if (value.Any(c => c <= ' ' || c >= '\u007f' || c is '"' or ',' or ';' or '\\'))
            throw new SourceException("login_required",
                $"Некорректное значение cookie {name}: передайте только её значение без кавычек и пробелов.");
        return new
        {
            name,
            value,
            domain = ".x.com",
            path = "/",
            expires = -1,
            httpOnly,
            secure = true,
            sameSite = "Lax"
        };
    }
}
