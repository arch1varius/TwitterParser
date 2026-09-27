using Microsoft.Extensions.Options;
using Parser.Core;
using Parser.Infrastructure;
using TL;
using Xunit;

namespace Parser.Tests;

public sealed class TelegramTests
{
    internal static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    internal const string Text = "один два три четыре пять шесть семь восемь девять десять одиннадцать";
    private static readonly SourceRequest Request = new("channel", Start, Start.AddDays(1), PostSourceKind.Telegram);

    [Theory]
    [InlineData(" @My_Channel ", "my_channel")]
    [InlineData("https://t.me/My_Channel/", "my_channel")]
    [InlineData("abcdefghijklmnopqrstuvxyz_123456", "abcdefghijklmnopqrstuvxyz_123456")]
    [InlineData("abcdefghijklmnopqrstuvxyz_1234567", null)]
    [InlineData("-1001234567890", "-1001234567890")]
    [InlineData("https://t.me/channel/123", null)]
    [InlineData("https://t.me/+invite", null)]
    [InlineData("https://t.me.evil.test/channel", null)]
    [InlineData("https://t.me:8443/channel", null)]
    [InlineData("https://t.me/channel?x=1", null)]
    [InlineData("https://t.me/@channel", null)]
    [InlineData("../channel", null)]
    [InlineData("@@channel", null)]
    [InlineData("", null)]
    public void ChannelInputIsNormalized(string value, string? expected) =>
        Assert.Equal(expected, SourceKinds.NormalizeAuthor(value, PostSourceKind.Telegram));

    [Theory]
    [InlineData(9, FilterResult.TooShort)]
    [InlineData(10, FilterResult.TooShort)]
    [InlineData(11, FilterResult.Accept)]
    public void TelegramRequiresMoreThanTenWords(int words, FilterResult expected)
    {
        var message = Message(3, Start, string.Join(" ", Enumerable.Repeat("слово", words)) + " https://t.me/channel 😎");
        var post = TelegramPostSource.ConvertMessage(Channel(), message, Request.Author);
        Assert.Equal(expected, PostRules.Evaluate(post, Request));
        if (expected == FilterResult.TooShort)
            Assert.Contains("минимум 11", PostRules.CreateLog(Guid.NewGuid(), post, Request, expected).Detail);
        Assert.Equal(FilterResult.WrongAuthor, PostRules.Evaluate(post, Request with { Source = PostSourceKind.X }));
    }

    [Fact]
    public void TextAndCaptionsAreKeptWithoutAttachmentsAndIdsIncludeChannel()
    {
        foreach (var media in new MessageMedia[] { new MessageMediaPhoto(), new MessageMediaDocument(), new MessageMediaWebPage() })
        {
            var message = Message(17, Start, Text);
            message.media = media;
            message.fwd_from = new MessageFwdHeader();
            var post = TelegramPostSource.ConvertMessage(Channel(), message, "channel");
            Assert.Equal(Text, post.Text);
            Assert.Equal("42:17", post.Id);
            Assert.Equal("42", post.AuthorId);
            Assert.Equal("https://t.me/channel/17", post.Url);
            Assert.Equal(Start, post.PublishedAt);
            Assert.True(post.IsRepost);
            Assert.Empty(post.Photos);
            Assert.Equal(FilterResult.Accept, PostRules.Evaluate(post, Request));
            var other = TelegramPostSource.ConvertMessage(Channel(43), message, "other");
            Assert.NotEqual(post.Id, other.Id);
        }
        var privateChannel = Channel(); privateChannel.username = null;
        Assert.Equal("https://t.me/c/42/17", TelegramPostSource.ConvertMessage(privateChannel, Message(17, Start), "-10042").Url);
    }

    [Fact]
    public async Task PaginationUsesAllMessageIdsAndStopsAtPeriodStart()
    {
        var fake = new FakeTelegramClient
        {
            Pages = [
                [Message(10, Start.AddHours(3)), new MessageService { id = 9 }, new MessageEmpty { id = 8 }],
                [Message(7, Start), Message(6, Start.AddSeconds(-1))],
                [Message(5, Start.AddDays(-1))]
            ]
        };
        var pages = await Read(fake);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(new[] { 0, 8 }, fake.Calls.Select(c => c.Offset));
        Assert.Equal(Request.To.UtcDateTime, fake.Calls[0].Date);
        Assert.Equal(default, fake.Calls[1].Date);
        Assert.Equal("PeriodStartReached", pages[^1].StopReason);
        Assert.Equal(2, pages.SelectMany(p => p.Posts).Count(p => PostRules.Evaluate(p, Request) == FilterResult.Accept));
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task FractionalEndKeepsMessagesInLastSecondAndFilterRemainsHalfOpen()
    {
        var end = Start.AddSeconds(1).AddMilliseconds(500);
        var fake = new FakeTelegramClient { Pages = [[Message(2, Start.AddSeconds(1)), Message(1, Start.AddSeconds(2))], []] };
        var request = Request with { To = end };
        var pages = await Read(fake, request: request);
        Assert.Equal(Start.AddSeconds(2).UtcDateTime, fake.Calls[0].Date);
        Assert.Equal(1, pages.SelectMany(p => p.Posts).Count(p => PostRules.Evaluate(p, request) == FilterResult.Accept));
    }

    [Fact]
    public async Task EmptyHistoryAndPageLimitHaveDifferentStopReasons()
    {
        Assert.Equal("SourceFinished", (await Read(new FakeTelegramClient { Pages = [[]] }))[^1].StopReason);
        var fake = new FakeTelegramClient { Pages = [[Message(10, Start)]] };
        Assert.Equal("PageLimit", (await Read(fake, maxPages: 1))[^1].StopReason);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task RepeatedPageFailsInsteadOfLoopingOrReportingComplete()
    {
        var fake = new FakeTelegramClient { Pages = [[Message(10, Start)], [Message(10, Start)]] };
        Assert.Equal("telegram_pagination_stalled", (await Assert.ThrowsAsync<SourceException>(() => Read(fake))).Code);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task CancellationInterruptsHistoryAndDisposesSession()
    {
        var fake = new FakeTelegramClient { Block = true };
        using var cts = new CancellationTokenSource();
        var task = Read(fake, ct: cts.Token);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void BackgroundLoginNeverPromptsAndMissingSessionHasExplicitError()
    {
        var options = new TelegramOptions { ApiId = 123, ApiHash = new string('a', 32), SessionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".session") };
        Assert.Equal("login_required", Assert.Throws<SourceException>(() => TelegramSession.Create(options, new())).Code);
        foreach (var key in new[] { "phone_number", "verification_code", "password", "email", "email_verification_code" })
            Assert.Equal("login_required", Assert.Throws<SourceException>(() => TelegramSession.ConfigurationValue(key, options, options.SessionPath, false)).Code);
        Assert.Equal("-1", TelegramSession.ConfigurationValue("user_id", options, options.SessionPath, false));
        Assert.Equal("telegram_not_configured", Assert.Throws<SourceException>(() => new TelegramOptions().Validate()).Code);
        Assert.Equal("login_required", TelegramHistoryClient.Translate(new RpcException(401, "AUTH_KEY_UNREGISTERED")).Code);
        Assert.Equal("telegram_rate_limited", TelegramHistoryClient.Translate(new RpcException(420, "FLOOD_WAIT_60")).Code);
    }

    private static async Task<List<SourcePage>> Read(FakeTelegramClient fake, int maxPages = 10,
        SourceRequest? request = null, CancellationToken ct = default)
    {
        var source = new TelegramPostSource(new FakeTelegramFactory(() => fake), Options.Create(new TelegramOptions { MaxPages = maxPages, PageDelayMs = 0 }));
        var result = new List<SourcePage>();
        await foreach (var page in source.ReadAsync(request ?? Request, ct)) result.Add(page);
        return result;
    }

    internal static Channel Channel(long id = 42) => new() { id = id, username = "channel", title = "Test channel", flags = TL.Channel.Flags.broadcast };
    internal static Message Message(int id, DateTimeOffset date, string text = Text) => new()
        { id = id, date = date.UtcDateTime, message = text, peer_id = new PeerChannel { channel_id = 42 } };
}

internal sealed class FakeTelegramFactory(Func<FakeTelegramClient> create) : ITelegramHistoryClientFactory
{
    public ITelegramHistoryClient Create() => create();
}

internal sealed class FakeTelegramClient : ITelegramHistoryClient
{
    public IReadOnlyList<MessageBase>[] Pages { get; init; } = [];
    public List<(int Offset, DateTime Date)> Calls { get; } = [];
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Block { get; init; }
    public bool Disposed { get; private set; }
    private bool slow;
    public Task<Channel> ResolveAsync(string author, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (author == "login") throw TelegramSession.LoginRequired();
        slow = author == "slow";
        return Task.FromResult(TelegramTests.Channel());
    }
    public async Task<IReadOnlyList<MessageBase>> ReadAsync(Channel channel, int offsetId, DateTime offsetDate, CancellationToken ct)
    {
        Calls.Add((offsetId, offsetDate));
        Started.TrySetResult();
        if (Block || slow) await Task.Delay(Timeout.Infinite, ct);
        return Calls.Count <= Pages.Length ? Pages[Calls.Count - 1] : [];
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
