using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Parser.Core;

namespace Parser.Infrastructure;

public sealed class PlaywrightPostSource(IOptions<ParserOptions> options,
    ILogger<PlaywrightPostSource>? logger = null) : IPostSource
{
    private readonly ParserOptions settings = options.Value;

    public async IAsyncEnumerable<SourcePage> ReadAsync(SourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var contextOptions = XBrowserSession.CreateContextOptions(settings);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = settings.Headless });
        await using var context = await browser.NewContextAsync(contextOptions);
        await context.RouteAsync("**/*", route => route.Request.ResourceType is "media" or "image" or "font"
            || route.Request.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || route.Request.Url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
            ? route.AbortAsync() : route.ContinueAsync());

        var page = await context.NewPageAsync();
        var pending = new ConcurrentQueue<Task<IReadOnlyList<SourcePost>>>();
        var seen = new HashSet<string>();
        var graphqlResponses = new ConcurrentDictionary<string, int>();
        var timelineResponses = 0;
        page.Response += (_, response) =>
        {
            var operation = XTimelineResponse.GetOperation(response.Url);
            if (operation is null) return;
            graphqlResponses.AddOrUpdate($"{operation}: HTTP {response.Status}", 1, (_, count) => count + 1);
            if (XTimelineResponse.IsTimeline(response.Url))
            {
                Interlocked.Increment(ref timelineResponses);
                pending.Enqueue(XTimelineResponse.ReadAsync(response));
            }
        };
        using var registration = cancellationToken.Register(() => _ = CloseQuietlyAsync(browser));
        try
        {
            var hitScrollLimit = false;
            foreach (var suffix in new[] { "", "/with_replies" })
            {
                var feedSeen = new HashSet<string>();
                await page.GotoAsync($"https://x.com/{request.Author}{suffix}", new()
                { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
                var idle = 0;
                for (var scroll = 0; scroll < settings.MaxScrolls; scroll++)
                {
                    await Task.Delay(settings.ScrollDelayMs, cancellationToken);
                    if (page.Url.Contains("/i/flow/login", StringComparison.Ordinal)
                        || page.Url.Contains("/account/access", StringComparison.Ordinal))
                        throw new SourceException("login_required", "Сессия X истекла или требует подтверждения входа.");

                    var batch = new List<SourcePost>();
                    var newInFeed = 0;
                    while (pending.TryDequeue(out var task))
                    {
                        var posts = await task.WaitAsync(cancellationToken);
                        logger?.LogInformation("X timeline response: parsed {PostCount} posts.", posts.Count);
                        foreach (var post in posts)
                        {
                            if (feedSeen.Add(post.Id)) newInFeed++;
                            if (seen.Add(post.Id)) batch.Add(post);
                        }
                    }
                    yield return new SourcePage(batch);
                    idle = newInFeed == 0 ? idle + 1 : 0;
                    if (idle >= 5)
                    {
                        // Accounts can have replies even when their main profile feed is empty.
                        if (seen.Count == 0 && suffix.Length == 0) break;
                        if (seen.Count == 0)
                        {
                            var renderedPosts = await page.Locator("article[data-testid='tweet']").CountAsync();
                            var operations = string.Join(", ", graphqlResponses.OrderBy(p => p.Key)
                                .Take(12).Select(p => $"{p.Key} ({p.Value})"));
                            var detail = timelineResponses == 0
                                ? "Ответы ленты UserTweets/UserTweetsAndReplies/UserOriginalsTimeline не перехвачены."
                                : "Ответы ленты получены, но ни одного поста не распознано.";
                            detail += $" Карточек постов на странице: {renderedPosts}. Ответов ленты: {timelineResponses}."
                                + $" GraphQL: {(operations.Length == 0 ? "ответов нет" : operations)}.";
                            logger?.LogWarning("X collection returned no posts: {Detail}", detail);
                            throw new SourceException("source_unavailable", detail);
                        }
                        break;
                    }
                    if (scroll == settings.MaxScrolls - 1) hitScrollLimit = true;
                    await page.Mouse.WheelAsync(0, 1800);
                }
            }
            yield return new SourcePage([], hitScrollLimit ? "ScrollLimit" : "NoMoreVisibleItems");
        }
        finally
        {
            // Observe pending failures even if cancellation closes the browser first.
            while (pending.TryDequeue(out var task))
                try { await task; } catch { /* Main job already owns the terminal error. */ }
        }
    }

    private static async Task CloseQuietlyAsync(IBrowser browser)
    {
        try { await browser.CloseAsync(); }
        catch (PlaywrightException) { /* Already closed during cancellation. */ }
    }

    public static async Task LoginAsync(ParserOptions settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.AuthenticationPath))!);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = false });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://x.com/i/flow/login");
        Console.WriteLine("Войдите в X в открытом браузере, затем нажмите Enter здесь. Пароль приложение не сохраняет.");
        Console.ReadLine();
        var cookies = await context.CookiesAsync("https://x.com");
        if (!cookies.Any(c => c.Name == "auth_token"))
            throw new InvalidOperationException("Вход не завершён: сессионная cookie не найдена.");
        await context.StorageStateAsync(new() { Path = settings.AuthenticationPath });
        Console.WriteLine("Сессия сохранена в серверном каталоге данных.");
    }
}
