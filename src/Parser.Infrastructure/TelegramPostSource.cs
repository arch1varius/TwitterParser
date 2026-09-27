using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Parser.Core;
using TL;

namespace Parser.Infrastructure;

public sealed class TelegramPostSource(ITelegramHistoryClientFactory clients, IOptions<TelegramOptions> options) : IPostSource
{
    public async IAsyncEnumerable<SourcePage> ReadAsync(SourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var client = clients.Create();
        var channel = await client.ResolveAsync(request.Author, cancellationToken);
        var offsetId = 0;
        // Telegram dates have second precision. Round upwards so a fractional upper boundary doesn't lose a post.
        var seconds = request.To.ToUnixTimeSeconds();
        var offsetDate = DateTimeOffset.FromUnixTimeSeconds(seconds + (request.To.Ticks % TimeSpan.TicksPerSecond == 0 ? 0 : 1)).UtcDateTime;
        for (var page = 0; page < options.Value.MaxPages; page++)
        {
            var messages = await client.ReadAsync(channel, offsetId, offsetDate, cancellationToken);
            if (messages.Count == 0) { yield return new SourcePage([], "SourceFinished"); yield break; }
            var nextId = messages.Where(m => m.ID > 0).Select(m => m.ID).DefaultIfEmpty(0).Min();
            if (nextId == 0 || (offsetId != 0 && nextId >= offsetId))
                throw new SourceException("telegram_pagination_stalled", "Telegram вернул страницу без продвижения по истории.");
            var posts = messages.OfType<Message>().Where(m => m.peer_id is PeerChannel peer && peer.channel_id == channel.id)
                .Select(m => ConvertMessage(channel, m, request.Author)).ToArray();
            var beforePeriod = posts.Any(p => p.PublishedAt < request.From);
            yield return new SourcePage(posts, beforePeriod ? "PeriodStartReached" : null);
            if (beforePeriod) yield break;
            offsetId = nextId;
            offsetDate = default;
            if (page + 1 < options.Value.MaxPages)
                await Task.Delay(options.Value.PageDelayMs, cancellationToken);
        }
        yield return new SourcePage([], "PageLimit");
    }

    public static SourcePost ConvertMessage(Channel channel, Message message, string requestedAuthor)
    {
        var channelId = channel.id.ToString(CultureInfo.InvariantCulture);
        var id = message.id.ToString(CultureInfo.InvariantCulture);
        var username = SourceKinds.NormalizeAuthor(channel.username, PostSourceKind.Telegram);
        var url = username is null ? $"https://t.me/c/{channelId}/{id}" : $"https://t.me/{username}/{id}";
        // The message field contains both ordinary text and media captions. Never fetch the media itself.
        return new SourcePost($"{channelId}:{id}", channelId, requestedAuthor, channel.title,
            message.message ?? "", new DateTimeOffset(DateTime.SpecifyKind(message.date, DateTimeKind.Utc)),
            false, message.reply_to is not null, message.fwd_from is not null, false, true, [],
            PostSourceKind.Telegram, url);
    }
}
