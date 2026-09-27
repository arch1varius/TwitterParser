using System.Globalization;
using Microsoft.Extensions.Options;
using Parser.Core;
using TL;
using WTelegram;

namespace Parser.Infrastructure;

public interface ITelegramHistoryClient : IAsyncDisposable
{
    Task<Channel> ResolveAsync(string author, CancellationToken ct);
    Task<IReadOnlyList<MessageBase>> ReadAsync(Channel channel, int offsetId, DateTime offsetDate, CancellationToken ct);
}

public interface ITelegramHistoryClientFactory
{
    ITelegramHistoryClient Create();
}

public sealed class TelegramHistoryClientFactory(IOptions<TelegramOptions> options, IOptions<ParserOptions> parser)
    : ITelegramHistoryClientFactory
{
    public ITelegramHistoryClient Create() => new TelegramHistoryClient(TelegramSession.Create(options.Value, parser.Value));
}

public sealed class TelegramHistoryClient(Client client) : ITelegramHistoryClient
{
    public async Task<Channel> ResolveAsync(string author, CancellationToken ct)
    {
        await InvokeAsync(() => client.LoginUserIfNeeded(reloginOnFailedResume: false), ct);
        ChatBase? chat;
        if (author.StartsWith("-100", StringComparison.Ordinal))
        {
            var id = long.Parse(author.AsSpan(4), CultureInfo.InvariantCulture);
            var dialogs = await InvokeAsync(() => client.Messages_GetAllDialogs(), ct);
            dialogs.chats.TryGetValue(id, out chat);
        }
        else
        {
            var resolved = await InvokeAsync(() => client.Contacts_ResolveUsername(author), ct);
            chat = resolved.peer is PeerChannel peer && resolved.chats.TryGetValue(peer.channel_id, out var channel)
                ? channel : null;
        }
        if (chat is not Channel result || !result.flags.HasFlag(Channel.Flags.broadcast))
            throw new SourceException("telegram_channel_unavailable", "Канал не найден или недоступен. Укажите канал, а не пользователя или группу; для закрытого канала аккаунт должен быть его участником.");
        return result;
    }

    public async Task<IReadOnlyList<MessageBase>> ReadAsync(Channel channel, int offsetId, DateTime offsetDate, CancellationToken ct)
    {
        var history = await InvokeAsync(() => client.Messages_GetHistory(channel, offset_id: offsetId,
            offset_date: offsetDate, limit: 100), ct);
        return history.Messages;
    }

    private static async Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return await action().WaitAsync(ct); }
        catch (RpcException ex) { throw Translate(ex); }
    }

    public static SourceException Translate(RpcException ex) => ex.Code switch
    {
        401 => TelegramSession.LoginRequired(),
        420 => new("telegram_rate_limited", $"Telegram ограничил частоту запросов. Повторите задание не раньше чем через {ex.X} секунд."),
        _ => new("telegram_source_error", $"Telegram отклонил запрос ({ex.Code}). Проверьте имя канала и доступ аккаунта к его истории.")
    };

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
