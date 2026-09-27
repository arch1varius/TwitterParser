using System.Text.RegularExpressions;

namespace Parser.Core;

public static partial class SourceKinds
{
    public static PostSourceKind? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "x" => PostSourceKind.X,
        "telegram" => PostSourceKind.Telegram,
        _ => null
    };

    public static string ToApiValue(this PostSourceKind source) => source switch
    {
        PostSourceKind.X => "x",
        PostSourceKind.Telegram => "telegram",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex TelegramUsername();

    [GeneratedRegex("^-100[1-9][0-9]{0,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex TelegramChannelId();

    public static string? NormalizeAuthor(string? value, PostSourceKind source)
    {
        if (source == PostSourceKind.X) return PostRules.NormalizeAuthor(value);
        if (source != PostSourceKind.Telegram) return null;
        var name = value?.Trim();
        if (name is null) return null;
        if (Uri.TryCreate(name, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Host != "t.me" || !uri.IsDefaultPort
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return null;
            name = uri.AbsolutePath.Trim('/');
            return TelegramUsername().IsMatch(name) ? name.ToLowerInvariant() : null;
        }
        if (name.StartsWith('@')) name = name[1..];
        return TelegramUsername().IsMatch(name) || TelegramChannelId().IsMatch(name)
            ? name.ToLowerInvariant() : null;
    }
}
