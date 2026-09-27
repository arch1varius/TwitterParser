using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Parser.Contracts;
using Parser.Core;
using Parser.Infrastructure;
using Xunit;

namespace Parser.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARSER_TEST_CONNECTION")))
            Skip = "Set PARSER_TEST_CONNECTION to an isolated PostgreSQL admin connection.";
    }
}

public sealed class IntegrationTests
{
    [PostgresFact]
    public async Task TelegramJobsUseHistorySourceAndSupportTextOnlyFilteringDeduplicationAndReading()
    {
        await using var host = await TestHost.CreateAsync();
        var from = TelegramTests.Start;
        var request = new CreateParseJobRequest("https://t.me/Channel/", from, from.AddDays(1), "telegram");
        var job = await Wait(host.Client, (await Start(host.Client, request)).Id);
        Assert.Equal("Completed", job.Status);
        Assert.Equal("telegram", job.Source);
        Assert.Equal("channel", job.Author);
        Assert.Equal(1, job.Saved);
        Assert.Equal(2, job.SkippedOther);
        Assert.Equal("PeriodStartReached", job.StopReason);
        var again = await Wait(host.Client, (await Start(host.Client, request)).Id);
        Assert.Equal(1, again.Existing);
        Assert.Equal(0, again.Saved);
        var author = Assert.Single((await host.Client.GetFromJsonAsync<List<AuthorDto>>("/api/authors?source=telegram"))!);
        Assert.Equal("telegram", author.Source);
        Assert.Empty((await host.Client.GetFromJsonAsync<List<AuthorDto>>("/api/authors?source=x"))!);
        var query = $"authorId={author.Id}&from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z";
        var post = Assert.Single((await host.Client.GetFromJsonAsync<PostPage>($"/api/posts?{query}"))!.Items);
        Assert.Equal("telegram", post.Source);
        Assert.Equal("42:3", post.SourceId);
        Assert.Equal("https://t.me/channel/3", post.Url);
        Assert.Equal(TelegramTests.Text, post.Text);
        Assert.Empty(post.Photos);
        Assert.Equal(post.Id, (await host.Client.GetFromJsonAsync<PostDto>($"/api/posts/random?{query}"))!.Id);
        var logs = (await host.Client.GetFromJsonAsync<ParseJobLogPage>($"/api/parse-jobs/{job.Id}/logs?reason=TooShort"))!;
        Assert.Equal(10, Assert.Single(logs.Items).WordCount);
        Assert.Contains("минимум 11", logs.Items[0].Detail);
        Assert.Equal(0, host.Photos.DownloadAttempts);
        var missingLogin = await Wait(host.Client, (await Start(host.Client, request with { Author = "login" })).Id);
        Assert.Equal("NeedsLogin", missingLogin.Status);
        var longName = await Wait(host.Client, (await Start(host.Client, request with { Author = new string('a', 32) })).Id);
        Assert.Equal("Completed", longName.Status);
        foreach (var invalid in new[] { request with { Source = "unknown" }, request with { Author = "https://example.com/channel" }, request with { From = request.To } })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/parse-jobs", invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/authors?source=unknown")).StatusCode);
        var slow = await Start(host.Client, request with { Author = "slow" });
        await WaitStatus(host.Client, slow.Id, "Running");
        await host.Client.PostAsync($"/api/parse-jobs/{slow.Id}/cancel", null);
        Assert.Equal("Cancelled", (await Wait(host.Client, slow.Id)).Status);
        await host.Factory.DisposeAsync();
        host.Restart();
        Assert.Equal("telegram", (await host.Client.GetFromJsonAsync<ParseJobDto>($"/api/parse-jobs/{job.Id}"))!.Source);
        Assert.Equal(post.Id, (await host.Client.GetFromJsonAsync<PostDto>($"/api/posts/random?{query}"))!.Id);
    }

    [PostgresFact]
    public async Task MigrationPreservesExistingXDataAndAllowsIdenticalIdsAcrossSources()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Factory.DisposeAsync();
        await using var db = new ParserDbContext(new DbContextOptionsBuilder<ParserDbContext>().UseNpgsql(host.Connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260924120000_AddJobLogs");
        var authorId = Guid.NewGuid(); var postId = Guid.NewGuid(); var date = TelegramTests.Start;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Authors" ("Id", "SourceId", "Username", "DisplayName") VALUES ({authorId}, '42', 'channel', 'X author');
            INSERT INTO "Posts" ("Id", "SourceId", "AuthorId", "Text", "Url", "PublishedAt", "CollectedAt", "Status")
            VALUES ({postId}, '42:3', {authorId}, {TelegramTests.Text}, 'https://x.com/channel/status/3', {date}, {date}, 'Ready');
            """);
        await db.Database.MigrateAsync();
        var old = await db.Posts.Include(p => p.Author).SingleAsync();
        Assert.Equal(postId, old.Id);
        Assert.Equal(PostSourceKind.X, old.Source);
        Assert.Equal(PostSourceKind.X, old.Author.Source);
        Assert.Equal(TelegramTests.Text, old.Text);
        var importer = new PostImporter(db, Microsoft.Extensions.Options.Options.Create(new ParserOptions()));
        var tg = TelegramPostSource.ConvertMessage(TelegramTests.Channel(), TelegramTests.Message(3, date), "channel");
        Assert.Equal(ImportResult.Saved, await importer.ImportAsync(tg, default));
        Assert.Equal(ImportResult.Existing, await importer.ImportAsync(tg, default));
        Assert.Equal(2, await db.Posts.CountAsync());
        Assert.Equal(2, await db.Authors.CountAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [PostgresFact]
    public async Task JobLogsPersistReasonsSupportPagingAndStayIsolated()
    {
        await using var host = await TestHost.CreateAsync();
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var job = await Wait(host.Client, (await Start(host.Client, new("logs", from, from.AddDays(1)))).Id);
        Assert.Equal("Completed", job.Status);
        Assert.Equal(6, job.Scanned);
        Assert.Equal(4, job.SkippedOther);
        Assert.Equal(1, job.Errors);
        var path = $"/api/parse-jobs/{job.Id}/logs";
        var all = (await host.Client.GetFromJsonAsync<ParseJobLogPage>(path))!;
        Assert.Equal(5, all.Total);
        Assert.Equal(5, all.Items.Count);
        Assert.Equal(job.SkippedOther, all.Items.Count(entry => !entry.IsError));
        Assert.Equal(job.Errors, all.Items.Count(entry => entry.IsError));
        Assert.Equal(2, all.Reasons.Single(item => item.Reason == "OutsidePeriod").Count);
        Assert.Equal(9, all.Items.Single(item => item.Reason == "TooShort").WordCount);
        Assert.All(all.Items, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Detail)));
        var first = (await host.Client.GetFromJsonAsync<ParseJobLogPage>(path + "?pageSize=2"))!;
        var second = (await host.Client.GetFromJsonAsync<ParseJobLogPage>(path + "?pageSize=2&page=2"))!;
        Assert.Equal(all.Items.Take(2).Select(entry => entry.Id), first.Items.Select(entry => entry.Id));
        Assert.Equal(all.Items.Skip(2).Take(2).Select(entry => entry.Id), second.Items.Select(entry => entry.Id));
        var filtered = (await host.Client.GetFromJsonAsync<ParseJobLogPage>(path + "?reason=OutsidePeriod"))!;
        Assert.Equal(2, filtered.Total);
        Assert.All(filtered.Items, entry => Assert.Equal("OutsidePeriod", entry.Reason));
        Assert.Equal(5, filtered.Reasons.Sum(item => item.Count));
        var another = await Wait(host.Client, (await Start(host.Client, new("logs", from, from.AddDays(1)))).Id);
        var otherLogs = (await host.Client.GetFromJsonAsync<ParseJobLogPage>($"/api/parse-jobs/{another.Id}/logs"))!;
        Assert.Equal(5, otherLogs.Total);
        Assert.Empty(all.Items.Select(entry => entry.Id).Intersect(otherLogs.Items.Select(entry => entry.Id)));
        foreach (var query in new[] { "?page=0", "?pageSize=101", "?reason=Accept", "?reason=999" })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync(path + query)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/parse-jobs/{Guid.NewGuid()}/logs")).StatusCode);
        using (var unauthorized = host.Factory.CreateClient())
            Assert.Equal(HttpStatusCode.Unauthorized, (await unauthorized.GetAsync(path)).StatusCode);
        await host.Factory.DisposeAsync();
        host.Restart();
        var persisted = (await host.Client.GetFromJsonAsync<ParseJobLogPage>(path))!;
        Assert.Equal(all.Items.Select(entry => entry.Id), persisted.Items.Select(entry => entry.Id));
    }

    [PostgresFact]
    public async Task ApiWorkerTextOnlyDuplicatesRandomAndAuthorizationWorkTogether()
    {
        await using var host = await TestHost.CreateAsync();
        using var client = host.Client;
        using var unauthorized = host.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await unauthorized.GetAsync("/api/authors")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var request = new CreateParseJobRequest("@Alice", from, from.AddDays(1));
        var job = await Start(client, request);
        job = await Wait(client, job.Id);
        Assert.Equal("Completed", job.Status);
        Assert.Equal(6, job.Saved);
        Assert.Equal(0, job.SkippedVideo);
        Assert.Equal(1, job.SkippedOther);
        Assert.Equal(1, job.Errors);
        var again = await Wait(client, (await Start(client, request)).Id);
        Assert.Equal(6, again.Existing);
        Assert.Equal(0, again.Saved);
        var author = Assert.Single((await client.GetFromJsonAsync<List<AuthorDto>>("/api/authors"))!);
        var query = $"authorId={author.Id}&from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z";
        var posts = (await client.GetFromJsonAsync<PostPage>($"/api/posts?{query}"))!;
        Assert.Equal(6, posts.Total);
        Assert.All(posts.Items, p => Assert.Empty(p.Photos));
        Assert.Equal(0, host.Photos.DownloadAttempts);
        Assert.All(posts.Items, p => Assert.True(PostRules.CountWords(p.Text) >= 10));
        for (var i = 0; i < 5; i++)
        {
            var random = (await client.GetFromJsonAsync<PostDto>(
                $"/api/posts/random?authorId={author.Id}&from=2026-01-01T00:00:00Z&to=2026-01-01T01:00:00Z"))!;
            Assert.Equal("alice1", random.SourceId); // Upper bound excludes the photo post exactly at 01:00.
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(
            $"/api/posts/random?authorId={author.Id}&from=2027-01-01T00:00:00Z&to=2027-01-02T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/parse-jobs",
            new CreateParseJobRequest("https://x.com/alice", from, from.AddDays(1)))).StatusCode);
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParserDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(6, await db.Posts.CountAsync());
    }

    [PostgresFact]
    public async Task PhotoPostIsReadyWithoutDownloadingOrCreatingPhotos()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Factory.Services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<PostImporter>();
        var db = scope.ServiceProvider.GetRequiredService<ParserDbContext>();
        var source = new SourcePost("retry-post", "retry-author", "retry", "Retry", "Photo",
            DateTimeOffset.UtcNow, false, false, false, false, true, [new("https://pbs.twimg.com/media/test.png", 1, 1)]);
        host.Photos.FailDownloads = true;
        Assert.Equal(ImportResult.Saved, await importer.ImportAsync(source, default));
        Assert.Equal(ImportResult.Existing, await importer.ImportAsync(source, default));
        Assert.Equal(1, await db.Posts.CountAsync());
        Assert.Equal(0, await db.Photos.CountAsync());
        Assert.Equal(0, host.Photos.DownloadAttempts);
        Assert.Equal(PostStatus.Ready, (await db.Posts.SingleAsync()).Status);
    }

    [PostgresFact]
    public async Task OldPendingPostBecomesReadyWithoutDownloadingMissingPhotos()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParserDbContext>();
        var published = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var author = new Author { SourceId = "old-author", Username = "old", DisplayName = "Old" };
        var post = new Post
        {
            SourceId = "old-post", Author = author, Text = "one two three four five six seven eight nine ten",
            PublishedAt = published, Status = PostStatus.PendingPhotos,
            Photos = [new() { StorageKey = "missing" }, new() { StorageKey = "downloaded", Position = 1,
                Downloaded = true, ContentType = "image/png" }]
        };
        db.Posts.Add(post);
        await db.SaveChangesAsync();
        host.Photos.FailDownloads = true;
        var source = new SourcePost(post.SourceId, author.SourceId, author.Username, author.DisplayName,
            post.Text, published, false, false, false, false, true, []);
        var importer = scope.ServiceProvider.GetRequiredService<PostImporter>();
        Assert.Equal(ImportResult.Saved, await importer.ImportAsync(source, default));
        Assert.Equal(PostStatus.Ready, post.Status);
        Assert.Equal(0, host.Photos.DownloadAttempts);
        var result = (await host.Client.GetFromJsonAsync<PostPage>(
            $"/api/posts?authorId={author.Id}&from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z"))!;
        var photo = Assert.Single(Assert.Single(result.Items).Photos);
        Assert.Equal(post.Photos.Single(p => p.Downloaded).Id, photo.Id);
        using var image = await host.Client.GetAsync(photo.Url);
        Assert.Equal(FakePhotoStore.Png, await image.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task CancellationStopsRunningAndQueuedJobs()
    {
        await using var host = await TestHost.CreateAsync();
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var first = await Start(host.Client, new("slow", from, from.AddDays(1)));
        await WaitStatus(host.Client, first.Id, "Running");
        var queued = await Start(host.Client, new("alice", from, from.AddDays(1)));
        await host.Client.PostAsync($"/api/parse-jobs/{queued.Id}/cancel", null);
        Assert.Equal("Cancelled", (await Wait(host.Client, queued.Id)).Status);
        await host.Client.PostAsync($"/api/parse-jobs/{first.Id}/cancel", null);
        Assert.Equal("Cancelled", (await Wait(host.Client, first.Id)).Status);
    }

    [PostgresFact]
    public async Task MissingLoginHasExplicitStatus()
    {
        await using var host = await TestHost.CreateAsync();
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var job = await Wait(host.Client, (await Start(host.Client, new("login", from, from.AddDays(1)))).Id);
        Assert.Equal("NeedsLogin", job.Status);
        Assert.Equal("login_required", job.ErrorCode);
    }

    [PostgresFact]
    public async Task PendingPhotosAreHiddenAndExpiredFilesAreDeleted()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParserDbContext>();
        var author = new Author { SourceId = "42", Username = "pending", DisplayName = "Pending" };
        var post = new Post { SourceId = "43", Author = author, Text = "pending", PublishedAt = DateTimeOffset.UtcNow,
            Status = PostStatus.PendingPhotos, Photos = [new() { StorageKey = Guid.NewGuid().ToString("N") }] };
        db.Posts.Add(post); await db.SaveChangesAsync();
        var query = $"authorId={author.Id}&from=2020-01-01T00:00:00Z&to=2030-01-01T00:00:00Z";
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/posts/random?{query}")).StatusCode);
        post.Status = PostStatus.Ready; post.Photos[0].Downloaded = true; post.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/posts/random?{query}")).StatusCode);
        await scope.ServiceProvider.GetRequiredService<RetentionService>().CleanAsync(default);
        Assert.False(await db.Posts.AnyAsync(p => p.Id == post.Id));
        Assert.False(await db.FileDeletions.AnyAsync());
        Assert.Contains(post.Photos[0].StorageKey, host.Photos.Deleted);
    }

    [PostgresFact]
    public async Task RestartRecoversInterruptedJobWithoutDuplicates()
    {
        await using var host = await TestHost.CreateAsync();
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await Wait(host.Client, (await Start(host.Client, new("alice", from, from.AddDays(1)))).Id);
        await host.Factory.DisposeAsync();
        Guid id;
        await using (var db = new ParserDbContext(new DbContextOptionsBuilder<ParserDbContext>().UseNpgsql(host.Connection).Options))
        {
            var job = new ParseJob { Author = "alice", From = from, To = from.AddDays(1), Status = JobStatus.Running };
            db.Jobs.Add(job); await db.SaveChangesAsync(); id = job.Id;
            db.JobLogs.Add(new ParseJobLog { JobId = id, Reason = FilterResult.WrongAuthor,
                PublishedAt = from, Detail = "Previous interrupted pass" });
            await db.SaveChangesAsync();
        }
        host.Restart();
        var result = await Wait(host.Client, id);
        Assert.Equal("Completed", result.Status);
        Assert.Equal(6, result.Existing);
        var logs = (await host.Client.GetFromJsonAsync<ParseJobLogPage>($"/api/parse-jobs/{id}/logs"))!;
        Assert.Equal(2, logs.Total);
        Assert.DoesNotContain(logs.Items, entry => entry.Detail == "Previous interrupted pass");
    }

    private static async Task<ParseJobDto> Start(HttpClient client, CreateParseJobRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/parse-jobs", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ParseJobDto>())!;
    }
    private static async Task<ParseJobDto> Wait(HttpClient client, Guid id)
    {
        for (var i = 0; i < 150; i++)
        {
            var job = (await client.GetFromJsonAsync<ParseJobDto>($"/api/parse-jobs/{id}"))!;
            if (job.Status is not ("Queued" or "Running" or "CancelRequested")) return job;
            await Task.Delay(100);
        }
        throw new TimeoutException("Job did not finish.");
    }
    private static async Task WaitStatus(HttpClient client, Guid id, string status)
    {
        for (var i = 0; i < 100; i++)
        {
            if ((await client.GetFromJsonAsync<ParseJobDto>($"/api/parse-jobs/{id}"))!.Status == status) return;
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }
}

internal sealed class TestHost : IAsyncDisposable
{
    private readonly string admin;
    private readonly string database = "parser_test_" + Guid.NewGuid().ToString("N");
    public string Connection { get; }
    public FakePhotoStore Photos { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    private TestHost(string admin)
    {
        this.admin = admin;
        Connection = new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString;
    }
    public static async Task<TestHost> CreateAsync()
    {
        var host = new TestHost(Environment.GetEnvironmentVariable("PARSER_TEST_CONNECTION")!);
        await using var connection = new NpgsqlConnection(host.admin); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{host.database}\"", connection);
        await command.ExecuteNonQueryAsync(); host.Restart(); return host;
    }
    public void Restart()
    {
        Client?.Dispose();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Parser"] = Connection,
                ["Api:Key"] = "integration-test-key",
                ["Parser:RetentionDays"] = null,
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:EventLog:LogLevel:Default"] = "None"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPostSource>(); services.AddScoped<IPostSource, FakeSource>();
                services.RemoveAll<ITelegramHistoryClientFactory>();
                services.AddSingleton<ITelegramHistoryClientFactory>(new FakeTelegramFactory(() => new FakeTelegramClient
                {
                    Pages = [[
                        TelegramTests.Message(3, TelegramTests.Start),
                        TelegramTests.Message(2, TelegramTests.Start, "one two three four five six seven eight nine ten"),
                        TelegramTests.Message(1, TelegramTests.Start.AddDays(-1))
                    ]]
                }));
                services.RemoveAll<IPhotoStore>(); services.AddSingleton<IPhotoStore>(Photos);
            });
        });
        Client = Factory.CreateClient(); Client.DefaultRequestHeaders.Add("X-Api-Key", "integration-test-key");
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose(); await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(admin); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed class FakeSource(TelegramPostSource telegram) : IPostSource
{
    public async IAsyncEnumerable<SourcePage> ReadAsync(SourceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request.Source == PostSourceKind.Telegram)
        {
            await foreach (var page in telegram.ReadAsync(request, cancellationToken)) yield return page;
            yield break;
        }
        if (request.Author == "slow") await Task.Delay(60000, cancellationToken);
        if (request.Author == "login") throw new SourceException("login_required", "Test login required.");
        var post = new SourcePost(request.Author + "1", request.Author + "id", request.Author, "Alice",
            "one two three four five six seven eight nine ten",
            request.From, false, false, false, false, true, []);
        if (request.Author == "logs")
        {
            var shortPost = post with { Id = "short", Text = "one two three four five six seven eight nine" };
            yield return new SourcePage([post, shortPost,
                post with { Id = "before", PublishedAt = request.From.AddTicks(-1) },
                post with { Id = "end", PublishedAt = request.To },
                post with { Id = "other", Username = "bob" },
                post with { Id = "incomplete", IsComplete = false }, shortPost], "FixtureFinished");
            yield break;
        }
        yield return new SourcePage([post,
            post with { Id = request.Author + "2", PublishedAt = request.From.AddHours(1), Photos = [new("https://pbs.twimg.com/media/test.png", 1, 1)] },
            post with { Id = request.Author + "3", PublishedAt = request.From.AddHours(2), HasVideo = true,
                Photos = [new("https://pbs.twimg.com/media/test.png", 1, 1)] },
            post with { Id = request.Author + "4", PublishedAt = request.From.AddHours(3), IsReply = true },
            post with { Id = request.Author + "5", IsComplete = false },
            post with { Id = request.Author + "6", Text = "one two three four five six seven eight nine" },
            post with { Id = request.Author + "7", PublishedAt = request.From.AddHours(4), IsQuote = true },
            post with { Id = request.Author + "8", PublishedAt = request.From.AddHours(5), IsRepost = true },
            post], "FixtureFinished");
    }
}

internal sealed class FakePhotoStore : IPhotoStore
{
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9X8AAAAASUVORK5CYII=");
    public List<string> Deleted { get; } = [];
    public bool FailDownloads { get; set; }
    public int DownloadAttempts { get; private set; }
    public Task<string> DownloadAsync(string sourceUrl, string storageKey, CancellationToken cancellationToken)
    {
        DownloadAttempts++;
        return FailDownloads ? Task.FromException<string>(new IOException("Test failure")) : Task.FromResult("image/png");
    }
    public Stream OpenRead(string storageKey) => new MemoryStream(Png);
    public void Delete(string storageKey) => Deleted.Add(storageKey);
}
