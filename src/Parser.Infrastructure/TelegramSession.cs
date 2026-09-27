using System.Globalization;
using System.Text;
using Parser.Core;
using WTelegram;

namespace Parser.Infrastructure;

public static class TelegramSession
{
    public static Client Create(TelegramOptions options, ParserOptions parser, bool interactive = false)
    {
        options.Validate();
        var path = options.AuthenticationPath(parser);
        if (!interactive && (!File.Exists(path) || new FileInfo(path).Length == 0))
            throw LoginRequired();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var client = new Client(key => ConfigurationValue(key, options, path, interactive));
        client.FloodRetryThreshold = 0; // Return a clear job error instead of an unbounded hidden retry.
        client.DisableUpdates();
        return client;
    }

    public static string? ConfigurationValue(string key, TelegramOptions options, string path, bool interactive) => key switch
    {
        "api_id" => options.ApiId.ToString(CultureInfo.InvariantCulture),
        "api_hash" => options.ApiHash,
        "session_pathname" => path,
        "user_id" => "-1", // Resume the account saved in this dedicated session without asking for a phone number.
        // In server mode never request/send a login code or fall back to WTelegram's console prompts.
        "phone_number" => interactive
            ? string.IsNullOrWhiteSpace(options.PhoneNumber) ? Prompt("Номер телефона (+…): ", false) : options.PhoneNumber
            : throw LoginRequired(),
        "verification_code" or "email_verification_code" => interactive ? Prompt("Код Telegram: ", true) : throw LoginRequired(),
        "password" => interactive ? Prompt("Пароль двухэтапной аутентификации: ", true) : throw LoginRequired(),
        "email" => interactive ? Prompt("Email для входа: ", false) : throw LoginRequired(),
        "first_name" or "last_name" => throw new SourceException("telegram_account_required", "Используйте существующий аккаунт Telegram."),
        _ => null
    };

    public static async Task LoginAsync(TelegramOptions options, ParserOptions parser)
    {
        await using var client = Create(options, parser, interactive: true);
        await client.LoginUserIfNeeded();
        Console.WriteLine($"Сессия Telegram сохранена: {options.AuthenticationPath(parser)}");
    }

    public static SourceException LoginRequired() => new("login_required",
        "Требуется вход в Telegram. Остановите API и выполните dotnet run --project src/Parser.Api -- --login-telegram.");

    private static string Prompt(string label, bool secret)
    {
        Console.Write(label);
        if (!secret || Console.IsInputRedirected)
            return Console.ReadLine() ?? throw new InvalidOperationException("Ввод завершён.");
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}
