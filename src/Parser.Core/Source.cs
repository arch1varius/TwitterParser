using System.Text.RegularExpressions;

namespace Parser.Core;

public sealed record SourcePhoto(string Url, int Width, int Height);
public sealed record SourcePost(string Id, string AuthorId, string Username, string DisplayName,
    string Text, DateTimeOffset PublishedAt, bool HasVideo, bool IsReply, bool IsRepost,
    bool IsQuote, bool IsComplete, IReadOnlyList<SourcePhoto> Photos,
    PostSourceKind Source = PostSourceKind.X, string? Url = null);
public sealed record SourcePage(IReadOnlyList<SourcePost> Posts, string? StopReason = null);
public sealed record SourceRequest(string Author, DateTimeOffset From, DateTimeOffset To,
    PostSourceKind Source = PostSourceKind.X);

public interface IPostSource
{
    IAsyncEnumerable<SourcePage> ReadAsync(SourceRequest request, CancellationToken cancellationToken);
}

public sealed class SourceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public enum FilterResult { Accept, Video, WrongAuthor, OutsidePeriod, Unsupported, Incomplete, TooShort }

public static partial class PostRules
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    public static int CountWords(string? text) => string.IsNullOrWhiteSpace(text)
        ? 0 : WordPattern().Count(UrlPattern().Replace(text, " "));

    public static string? NormalizeAuthor(string? value)
    {
        var username = value?.Trim().TrimStart('@');
        return username is not null && UsernamePattern().IsMatch(username)
            ? username.ToLowerInvariant() : null;
    }

    public static FilterResult Evaluate(SourcePost post, SourceRequest request)
    {
        if (post.Source != request.Source || !string.Equals(post.Username, request.Author, StringComparison.OrdinalIgnoreCase))
            return FilterResult.WrongAuthor;
        if (post.PublishedAt < request.From || post.PublishedAt >= request.To)
            return FilterResult.OutsidePeriod;
        if (!post.IsComplete || string.IsNullOrWhiteSpace(post.Id) || string.IsNullOrWhiteSpace(post.AuthorId))
            return FilterResult.Incomplete;
        if (CountWords(post.Text) < MinimumWords(request.Source)) return FilterResult.TooShort;
        return FilterResult.Accept;
    }

    public static ParseJobLog CreateLog(Guid jobId, SourcePost post, SourceRequest request, FilterResult reason)
    {
        var words = CountWords(post.Text);
        var detail = reason switch
        {
            FilterResult.WrongAuthor => $"Автор @{post.Username}; в задании выбран @{request.Author}.",
            FilterResult.OutsidePeriod => $"Дата поста {post.PublishedAt.ToUniversalTime():O} вне периода [{request.From.ToUniversalTime():O}, {request.To.ToUniversalTime():O}). Конец периода не включается.",
            FilterResult.TooShort => $"В тексте {words} слов; требуется минимум {MinimumWords(request.Source)}. HTTP(S)-ссылки и отдельные эмодзи не считаются словами.",
            FilterResult.Incomplete => "Неполные данные: " + string.Join("; ", new[]
            {
                !post.IsComplete ? "источник не предоставил полный текст или необходимые метаданные" : null,
                string.IsNullOrWhiteSpace(post.Id) ? "отсутствует ID поста" : null,
                string.IsNullOrWhiteSpace(post.AuthorId) ? "отсутствует ID автора" : null
            }.Where(value => value is not null)) + ".",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Expected a rejected post.")
        };
        // Do not split a surrogate pair at the preview boundary.
        var length = Math.Min(post.Text.Length, 500);
        if (length < post.Text.Length && length > 0 && char.IsHighSurrogate(post.Text[length - 1])) length--;
        return new ParseJobLog
        {
            JobId = jobId, SourceId = post.Id, Username = post.Username,
            PublishedAt = post.PublishedAt.ToUniversalTime(), TextPreview = post.Text[..length],
            WordCount = words, Reason = reason, Detail = detail
        };
    }

    public static int MinimumWords(PostSourceKind source) => source == PostSourceKind.Telegram ? 11 : 10;
}
