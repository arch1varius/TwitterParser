using Microsoft.Playwright;
using Parser.Core;

namespace Parser.Infrastructure;

public static class XTimelineResponse
{
    public static string? GetOperation(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host is not ("x.com" or "api.x.com")) return null;
        var path = uri.AbsolutePath;
        if (!path.Contains("/graphql/", StringComparison.Ordinal)) return null;
        var operation = path.TrimEnd('/').Split('/')[^1];
        // Only a bounded operation name can reach diagnostics; never include query strings.
        return operation.Length is > 0 and <= 80
            && operation.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? operation : null;
    }

    public static bool IsTimeline(string url) => GetOperation(url)
        is "UserTweets" or "UserTweetsAndReplies" or "UserOriginalsTimeline";

    public static async Task<IReadOnlyList<SourcePost>> ReadAsync(IResponse response)
    {
        if (response.Status is 401 or 403)
            throw new SourceException("login_required", "X отклонил текущую сессию.");
        if (response.Status == 429)
            throw new SourceException("rate_limited", "X ограничил частоту запросов. Повторите сбор позднее.");
        if (!response.Ok) throw new SourceException("source_http_error", $"X вернул HTTP {response.Status}.");
        try { return XResponseParser.Parse(await response.TextAsync()); }
        catch (System.Text.Json.JsonException)
        { throw new SourceException("source_format_changed", "Формат ответа X не распознан."); }
    }
}
