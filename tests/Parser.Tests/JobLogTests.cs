using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Parser.Core;
using Parser.Infrastructure;
using Xunit;

namespace Parser.Tests;

public sealed class JobLogTests
{
    private static readonly SourceRequest Request = new("alice", DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
    private static readonly SourcePost Post = new("123", "456", "alice", "Alice",
        "one two three four five six seven eight nine ten", Request.From, false, false, false, false, true, []);

    [Fact]
    public void DetailsExplainTheActualFilterDecision()
    {
        var job = Guid.NewGuid();
        var shortPost = Post with { Text = "one two https://t.co/a 😎" };
        var shortLog = PostRules.CreateLog(job, shortPost, Request, PostRules.Evaluate(shortPost, Request));
        Assert.Equal(job, shortLog.JobId);
        Assert.Equal(2, shortLog.WordCount);
        Assert.Equal(FilterResult.TooShort, shortLog.Reason);
        Assert.Contains("2 слов", shortLog.Detail);
        var wrong = Post with { Username = "bob", PublishedAt = Request.To, Text = "short" };
        var wrongLog = PostRules.CreateLog(job, wrong, Request, PostRules.Evaluate(wrong, Request));
        Assert.Equal(FilterResult.WrongAuthor, wrongLog.Reason);
        Assert.Contains("@bob", wrongLog.Detail);
        Assert.Contains("@alice", wrongLog.Detail);
        var outside = Post with { PublishedAt = Request.To };
        var outsideLog = PostRules.CreateLog(job, outside, Request, PostRules.Evaluate(outside, Request));
        Assert.Equal(FilterResult.OutsidePeriod, outsideLog.Reason);
        Assert.Contains(Request.To.ToString("O"), outsideLog.Detail);
        var incomplete = Post with { IsComplete = false, AuthorId = "" };
        var incompleteLog = PostRules.CreateLog(job, incomplete, Request, PostRules.Evaluate(incomplete, Request));
        Assert.Contains("ID автора", incompleteLog.Detail);
        Assert.Contains("полный текст", incompleteLog.Detail);
    }

    [Fact]
    public void PreviewIsBoundedWithoutSplittingEmoji()
    {
        var post = Post with { Text = new string('a', 499) + "😎 trailing", Username = "bob" };
        var log = PostRules.CreateLog(Guid.NewGuid(), post, Request, FilterResult.WrongAuthor);
        Assert.Equal(new string('a', 499), log.TextPreview);
    }

    [Fact]
    public void MigrationMatchesModelAndCreatesJobLogStorage()
    {
        using var db = new ParserDbContext(new DbContextOptionsBuilder<ParserDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript("20260921174126_Initial", "20260924120000_AddJobLogs");
        Assert.Contains("CREATE TABLE \"JobLogs\"", sql);
        Assert.Contains("ON DELETE CASCADE", sql);
        Assert.Contains("IX_JobLogs_JobId_Reason_Id", sql);
    }
}
