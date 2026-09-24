using System.Text.Json.Nodes;
using Parser.Core;
using Parser.Infrastructure;
using Xunit;

namespace Parser.Tests;

public sealed class RulesTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly SourceRequest Request = new("alice", Start, Start.AddDays(1));
    private static SourcePost Post => new("1", "2", "alice", "Alice", "one two three four five six seven eight nine ten", Start,
        false, false, false, false, true, []);

    [Theory]
    [InlineData(" @Alice_1 ", "alice_1")]
    [InlineData("https://x.com/alice", null)]
    [InlineData("../alice", null)]
    [InlineData("", null)]
    public void AuthorInputIsNormalizedAndNotAnArbitraryUrl(string value, string? expected) =>
        Assert.Equal(expected, PostRules.NormalizeAuthor(value));

    [Fact]
    public void PeriodIsHalfOpenAndOffsetIndependent()
    {
        Assert.Equal(FilterResult.Accept, PostRules.Evaluate(Post, Request));
        Assert.Equal(FilterResult.Accept, PostRules.Evaluate(Post with { PublishedAt = Start.ToOffset(TimeSpan.FromHours(3)) }, Request));
        Assert.Equal(FilterResult.OutsidePeriod, PostRules.Evaluate(Post with { PublishedAt = Request.To }, Request));
        Assert.Equal(FilterResult.OutsidePeriod, PostRules.Evaluate(Post with { PublishedAt = Start.AddTicks(-1) }, Request));
    }

    [Fact]
    public void VideoRepliesRepostsAndQuotesKeepTheirTextAndPhotos()
    {
        var photoPost = Post with { Photos = [new("https://pbs.twimg.com/media/a.jpg", 100, 100)] };
        Assert.Equal(FilterResult.Accept, PostRules.Evaluate(photoPost, Request));
        Assert.Equal(FilterResult.Accept, PostRules.Evaluate(photoPost with { HasVideo = true }, Request));
        Assert.Equal(FilterResult.Incomplete, PostRules.Evaluate(Post with { IsComplete = false }, Request));
        Assert.Equal(FilterResult.Accept, PostRules.Evaluate(Post with { IsReply = true }, Request));
        Assert.Equal(FilterResult.Accept, PostRules.Evaluate(Post with { IsRepost = true }, Request));
        Assert.Equal(FilterResult.Accept, PostRules.Evaluate(Post with { IsQuote = true }, Request));
        Assert.Equal(FilterResult.WrongAuthor, PostRules.Evaluate(Post with { Username = "bob" }, Request));
    }

    [Theory]
    [InlineData("one two three four five six seven eight nine", 9, FilterResult.TooShort)]
    [InlineData("one two three four five six seven eight nine ten", 10, FilterResult.Accept)]
    [InlineData("Один два три четыре пять шесть семь восемь девять десять", 10, FilterResult.Accept)]
    [InlineData("one\ntwo\tthree four five six seven eight nine https://t.co/abc 😎 !!!", 9, FilterResult.TooShort)]
    [InlineData("don't well-known #tag @name 2026", 5, FilterResult.TooShort)]
    [InlineData("", 0, FilterResult.TooShort)]
    public void MinimumTenWordsExcludesLinksAndStandaloneEmoji(string text, int count, FilterResult expected)
    {
        Assert.Equal(count, PostRules.CountWords(text));
        Assert.Equal(expected, PostRules.Evaluate(Post with { Text = text, HasVideo = true }, Request));
    }

    private const string TweetJson = """
    {"data":{"tweet":{"rest_id":"100","core":{"user_results":{"result":{
      "rest_id":"200","core":{"screen_name":"alice","name":"Alice"}}}},
      "legacy":{"created_at":"Thu Jan 01 00:00:00 +0000 2026","full_text":"Hello &amp; world",
      "truncated":false,"is_quote_status":false,"entities":{},
      "extended_entities":{"media":[{"type":"photo","media_url_https":"https://pbs.twimg.com/media/a.jpg",
      "original_info":{"width":1200,"height":800}}]}}}}}
    """;

    [Fact]
    public void BrowserResponsePreservesTextDateAndPhotoOrder()
    {
        var post = Assert.Single(XResponseParser.Parse(TweetJson));
        Assert.Equal(Start, post.PublishedAt);
        Assert.Equal("Hello & world", post.Text);
        Assert.Equal("alice", post.Username);
        Assert.True(post.IsComplete);
        Assert.Equal(1200, Assert.Single(post.Photos).Width);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("animated_gif")]
    public void BrowserResponseRecognizesMixedVideoAndGif(string type)
    {
        var node = JsonNode.Parse(TweetJson)!;
        node["data"]!["tweet"]!["legacy"]!["extended_entities"]!["media"]!.AsArray()
            .Add(new JsonObject { ["type"] = type });
        var post = Assert.Single(XResponseParser.Parse(node.ToJsonString()));
        Assert.True(post.HasVideo);
        Assert.Equal("https://pbs.twimg.com/media/a.jpg", Assert.Single(post.Photos).Url);
    }

    [Fact]
    public void LongPostUsesNoteTextAndUnresolvedNoteIsRejected()
    {
        var node = JsonNode.Parse(TweetJson)!;
        var tweet = node["data"]!["tweet"]!;
        tweet["legacy"]!["truncated"] = true;
        Assert.False(Assert.Single(XResponseParser.Parse(node.ToJsonString())).IsComplete);
        tweet["note_tweet"] = JsonNode.Parse("""{"note_tweet_results":{"result":{"text":"Complete long text"}}}""");
        var post = Assert.Single(XResponseParser.Parse(node.ToJsonString()));
        Assert.True(post.IsComplete);
        Assert.Equal("Complete long text", post.Text);
    }

    [Fact]
    public void EmbeddedQuotedPostIsNotImportedSeparately()
    {
        var node = JsonNode.Parse(TweetJson)!;
        node["data"]!["tweet"]!["quoted_status_result"] = JsonNode.Parse(TweetJson)!["data"]!.DeepClone();
        node["data"]!["tweet"]!["legacy"]!["is_quote_status"] = true;
        Assert.True(Assert.Single(XResponseParser.Parse(node.ToJsonString())).IsQuote);
    }

    [Fact]
    public void RepostUsesOriginalLongTextAndPhotosButKeepsRepostingAccountAndDate()
    {
        var node = JsonNode.Parse(TweetJson)!;
        var tweet = node["data"]!["tweet"]!;
        var original = tweet.DeepClone();
        original["rest_id"] = "original-id";
        original["core"]!["user_results"]!["result"]!["core"]!["screen_name"] = "bob";
        original["note_tweet"] = JsonNode.Parse("""{"note_tweet_results":{"result":{"text":"one two three four five six seven eight nine ten eleven"}}}""");
        tweet["legacy"]!["full_text"] = "RT @bob: shortened";
        tweet["legacy"]!["truncated"] = true;
        tweet["legacy"]!["retweeted_status_result"] = new JsonObject { ["result"] = original };
        var post = Assert.Single(XResponseParser.Parse(node.ToJsonString()));
        Assert.Equal("100", post.Id);
        Assert.Equal("alice", post.Username);
        Assert.Equal(Start, post.PublishedAt);
        Assert.True(post.IsRepost);
        Assert.True(post.IsComplete);
        Assert.Equal(11, PostRules.CountWords(post.Text));
        Assert.Single(post.Photos);
        Assert.Equal(FilterResult.Accept, PostRules.Evaluate(post, Request));
    }

    [Fact]
    public void PartialGraphQlErrorsDoNotDiscardAvailablePosts()
    {
        var node = JsonNode.Parse(TweetJson)!;
        node["errors"] = JsonNode.Parse("""[{"message":"unavailable unrelated entry","path":["data","other"]}]""");
        Assert.Single(XResponseParser.Parse(node.ToJsonString()));
    }

    [Theory]
    [InlineData("https://pbs.twimg.com/media/a.jpg", true)]
    [InlineData("http://pbs.twimg.com/media/a.jpg", false)]
    [InlineData("https://pbs.twimg.com.evil.test/media/a.jpg", false)]
    [InlineData("https://127.0.0.1/media/a.jpg", false)]
    [InlineData("https://pbs.twimg.com:8080/media/a.jpg", false)]
    public void PhotoOriginIsRestricted(string url, bool expected) => Assert.Equal(expected, PhotoStore.IsAllowedSource(url));
}
