using Microsoft.Playwright;
using Parser.Core;
using Parser.Infrastructure;
using Xunit;

namespace Parser.Tests;

public sealed class XTimelineResponseTests
{
    [Theory]
    [InlineData("https://x.com/i/api/graphql/hash/UserTweets?variables=abc", true)]
    [InlineData("https://x.com/i/api/graphql/hash/UserTweets", true)]
    [InlineData("https://api.x.com/graphql/hash/UserTweets?variables=abc", true)]
    [InlineData("https://api.x.com/graphql/hash/UserTweetsAndReplies", true)]
    [InlineData("https://x.com/i/api/graphql/hash/UserTweets/", true)]
    [InlineData("https://x.com/i/api/graphql/Yr8749ieoUptxRqQv766Fw/UserOriginalsTimeline?variables=abc", true)]
    [InlineData("https://api.x.com/graphql/hash/UserOriginalsTimeline", true)]
    [InlineData("https://x.com/i/api/graphql/hash/UserOriginalsTimelineExtra", false)]
    [InlineData("https://x.com/i/api/graphql/hash/UserByScreenName?next=/UserTweets?", false)]
    [InlineData("https://x.com/i/api/graphql/hash/HomeTimeline", false)]
    [InlineData("https://x.com/i/api/graphql/hash/UserTweetsExtra", false)]
    [InlineData("https://example.com/i/api/graphql/hash/UserTweets", false)]
    [InlineData("https://x.com/not-graphql/UserTweets", false)]
    public void TimelineMatchingUsesHostAndOperationInsteadOfQueryText(string url, bool expected)
        => Assert.Equal(expected, XTimelineResponse.IsTimeline(url));

    [Fact]
    public void OperationDiagnosticsExcludeQueryParameters()
    {
        var operation = XTimelineResponse.GetOperation("https://api.x.com/graphql/hash/UserTweets?secret=private-value");
        Assert.Equal("UserTweets", operation);
        Assert.Null(XTimelineResponse.GetOperation("https://x.com/graphql/hash/invalid%20operation?secret=private-value"));
    }

    [Fact]
    public void GraphQlErrorDoesNotLookLikeAnEmptyTimelineOrExposeRawMessages()
    {
        var error = Assert.Throws<SourceException>(() => XResponseParser.Parse(
            """{"errors":[{"code":123,"message":"private-response-details"}],"data":{}}"""));
        Assert.Equal("source_api_error", error.Code);
        Assert.Contains("123", error.Message);
        Assert.DoesNotContain("private-response-details", error.ToString());
        Assert.Empty(XResponseParser.Parse("""{"errors":[],"data":{}}"""));
    }

    [BrowserFact]
    public async Task BrowserResponsesAreParsedForBothApiPathsIncludingQuerylessRequests()
    {
        const string json = """
            {"data":{"user":{"result":{"timeline":{"timeline":{"instructions":[
            {"type":"TimelineAddEntries","entries":[{"content":{"itemContent":{"tweet_results":{"result":{
              "rest_id":"100","core":{"user_results":{"result":{
              "rest_id":"200","core":{"screen_name":"alice","name":"Alice"}}}},
              "legacy":{"created_at":"Thu Jan 01 00:00:00 +0000 2026","full_text":"A complete post",
              "truncated":false,"is_quote_status":false,"entities":{}}}}}}}]}
            ]}}}}}}
            """;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/*", route => route.FulfillAsync(new() { ContentType = "application/json", Body = json }));
        var pending = new List<Task<IReadOnlyList<SourcePost>>>();
        page.Response += (_, response) =>
        {
            if (XTimelineResponse.IsTimeline(response.Url)) pending.Add(XTimelineResponse.ReadAsync(response));
        };
        foreach (var url in new[]
        {
            "https://x.com/i/api/graphql/hash/UserTweets?variables=abc",
            "https://api.x.com/graphql/hash/UserTweets",
            "https://x.com/i/api/graphql/hash/UserTweetsAndReplies",
            "https://x.com/i/api/graphql/hash/UserOriginalsTimeline?variables=abc"
        })
            await page.GotoAsync(url);
        var batches = await Task.WhenAll(pending);
        Assert.Equal(4, batches.Length);
        foreach (var posts in batches)
        {
            var post = Assert.Single(posts);
            Assert.Equal("100", post.Id);
            Assert.Equal("A complete post", post.Text);
            Assert.True(post.IsComplete);
        }
    }
}
