namespace Parser.Infrastructure;

public sealed class TelegramOptions
{
    public int ApiId { get; set; }
    public string? ApiHash { get; set; }
    public string? PhoneNumber { get; set; }
    public string? SessionPath { get; set; }
    public int MaxPages { get; set; } = 1000;
    public int PageDelayMs { get; set; } = 1000;

    public string AuthenticationPath(ParserOptions parser) =>
        Path.GetFullPath(SessionPath ?? Path.Combine(parser.DataDirectory, "telegram.session"));

    public void Validate()
    {
        if (ApiId <= 0 || ApiHash is not { Length: 32 } || !ApiHash.All(Uri.IsHexDigit))
            throw new Parser.Core.SourceException("telegram_not_configured",
                "Задайте Telegram__ApiId и Telegram__ApiHash из https://my.telegram.org/apps.");
        if (MaxPages is < 1 or > 100000 || PageDelayMs is < 500 or > 60000)
            throw new Parser.Core.SourceException("telegram_invalid_limits",
                "Telegram:MaxPages должен быть 1–100000, PageDelayMs — 500–60000.");
    }
}
