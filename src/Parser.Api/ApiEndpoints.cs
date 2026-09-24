using Microsoft.EntityFrameworkCore;
using Parser.Contracts;
using Parser.Core;
using Parser.Infrastructure;

namespace Parser.Api;

public static class ApiEndpoints
{
    public static void MapParserApi(this WebApplication app)
    {
        app.MapGet("/health", async (ParserDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" })
                : Results.Problem(statusCode: 503, title: "database_unavailable"));

        app.MapPost("/api/parse-jobs", async (CreateParseJobRequest request, ParserDbContext db, CancellationToken ct) =>
        {
            var author = PostRules.NormalizeAuthor(request.Author);
            if (author is null || !ValidPeriod(request.From, request.To))
                return Problem(400, "invalid_request", "Укажите username автора и корректный период from < to.");
            var job = new ParseJob { Author = author, From = request.From.ToUniversalTime(), To = request.To.ToUniversalTime() };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/parse-jobs/{job.Id}", ToDto(job));
        });

        app.MapGet("/api/parse-jobs/{id:guid}", async (Guid id, ParserDbContext db, CancellationToken ct) =>
            await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct) is { } job
                ? Results.Ok(ToDto(job)) : Problem(404, "job_not_found", "Задание не найдено."));

        app.MapPost("/api/parse-jobs/{id:guid}/cancel", async (Guid id, ParserDbContext db, CancellationToken ct) =>
        {
            await db.Jobs.Where(j => j.Id == id && j.Status == JobStatus.Queued).ExecuteUpdateAsync(s =>
                s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.FinishedAt, DateTimeOffset.UtcNow), ct);
            await db.Jobs.Where(j => j.Id == id && j.Status == JobStatus.Running).ExecuteUpdateAsync(s =>
                s.SetProperty(j => j.Status, JobStatus.CancelRequested), ct);
            return await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct) is { } job
                ? Results.Ok(ToDto(job)) : Problem(404, "job_not_found", "Задание не найдено.");
        });

        app.MapGet("/api/parse-jobs/{id:guid}/logs", async (Guid id, int? page, int? pageSize,
            string? reason, ParserDbContext db, CancellationToken ct) =>
        {
            var number = page ?? 1;
            var size = pageSize ?? 50;
            FilterResult? filter = reason switch
            {
                null or "" => null,
                "WrongAuthor" => FilterResult.WrongAuthor,
                "OutsidePeriod" => FilterResult.OutsidePeriod,
                "TooShort" => FilterResult.TooShort,
                "Incomplete" => FilterResult.Incomplete,
                _ => FilterResult.Unsupported
            };
            if (number is < 1 or > 1000000 || size is < 1 or > 100 || filter == FilterResult.Unsupported)
                return Problem(400, "invalid_log_query", "Некорректная страница, размер страницы или причина пропуска.");
            if (!await db.Jobs.AnyAsync(job => job.Id == id, ct))
                return Problem(404, "job_not_found", "Задание не найдено.");
            // Keep the summary and page consistent while the worker appends logs or resets a pass.
            await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            var all = db.JobLogs.AsNoTracking().Where(entry => entry.JobId == id);
            var counts = await all.GroupBy(entry => entry.Reason)
                .Select(group => new { Reason = group.Key, Count = group.Count() }).ToListAsync(ct);
            var query = filter is null ? all : all.Where(entry => entry.Reason == filter.Value);
            var total = counts.Where(item => filter is null || item.Reason == filter.Value).Sum(item => item.Count);
            var entries = await query.OrderBy(entry => entry.Id).Skip((number - 1) * size).Take(size).ToListAsync(ct);
            await snapshot.CommitAsync(ct);
            return Results.Ok(new ParseJobLogPage(entries.Select(entry => new ParseJobLogDto(entry.Id,
                entry.CreatedAt, entry.SourceId, entry.Username, entry.PublishedAt, entry.TextPreview,
                entry.WordCount, entry.Reason.ToString(), entry.Detail, entry.Reason == FilterResult.Incomplete)).ToArray(),
                number, size, total, counts.OrderBy(item => item.Reason)
                    .Select(item => new ParseJobLogCount(item.Reason.ToString(), item.Count)).ToArray()));
        });

        app.MapGet("/api/authors", async (ParserDbContext db, CancellationToken ct) =>
            await db.Authors.AsNoTracking().OrderBy(a => a.Username)
                .Select(a => new AuthorDto(a.Id, a.SourceId, a.Username, a.DisplayName)).ToListAsync(ct));

        app.MapGet("/api/posts", async (Guid authorId, DateTimeOffset from, DateTimeOffset to,
            int? page, int? pageSize, ParserDbContext db, CancellationToken ct) =>
        {
            var number = page ?? 1;
            var size = pageSize ?? 20;
            if (!ValidPeriod(from, to) || number is < 1 or > 1000000 || size is < 1 or > 100)
                return Problem(400, "invalid_query", "Некорректный период или параметры страницы.");
            var query = Eligible(db, authorId, from, to);
            var count = await query.CountAsync(ct);
            var posts = await query.Include(p => p.Author).Include(p => p.Photos)
                .OrderByDescending(p => p.PublishedAt).ThenBy(p => p.Id)
                .Skip((number - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new PostPage(posts.Select(ToDto).ToArray(), number, size, count));
        });

        app.MapGet("/api/posts/random", async (Guid authorId, DateTimeOffset from, DateTimeOffset to,
            HttpResponse response, ParserDbContext db, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            if (!ValidPeriod(from, to)) return Problem(400, "invalid_period", "Ожидается from < to.");
            var query = Eligible(db, authorId, from, to);
            var id = await query.OrderBy(_ => EF.Functions.Random()).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            if (id is null) return Problem(404, "no_posts_in_period", "Нет загруженных постов за выбранный период.");
            var post = await query.Include(p => p.Author).Include(p => p.Photos).SingleOrDefaultAsync(p => p.Id == id, ct);
            return post is null ? Problem(404, "no_posts_in_period", "Пост больше не доступен.") : Results.Ok(ToDto(post));
        });

        app.MapGet("/api/photos/{id:guid}", async (Guid id, ParserDbContext db, IPhotoStore store, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var photo = await db.Photos.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.Downloaded
                && p.Post.Status == PostStatus.Ready && (p.Post.ExpiresAt == null || p.Post.ExpiresAt > now), ct);
            if (photo is null) return Problem(404, "photo_not_found", "Фотография не найдена.");
            try { return Results.File(store.OpenRead(photo.StorageKey), photo.ContentType, enableRangeProcessing: true); }
            catch (FileNotFoundException) { return Problem(404, "photo_not_found", "Файл фотографии не найден."); }
        });
    }

    private static bool ValidPeriod(DateTimeOffset from, DateTimeOffset to) =>
        from != default && to != default && from < to;

    private static IQueryable<Post> Eligible(ParserDbContext db, Guid authorId, DateTimeOffset from, DateTimeOffset to)
    {
        var now = DateTimeOffset.UtcNow;
        from = from.ToUniversalTime(); to = to.ToUniversalTime();
        return db.Posts.AsNoTracking().Where(p => p.AuthorId == authorId && p.Status == PostStatus.Ready
            && p.PublishedAt >= from && p.PublishedAt < to && (p.ExpiresAt == null || p.ExpiresAt > now));
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    public static ParseJobDto ToDto(ParseJob j) => new(j.Id, j.Author, j.From, j.To, j.Status.ToString(),
        j.Scanned, j.Saved, j.Existing, j.SkippedVideo, j.SkippedOther, j.Errors, j.CreatedAt,
        j.StartedAt, j.FinishedAt, j.EarliestSeenAt, j.StopReason, j.ErrorCode, j.ErrorMessage);

    private static PostDto ToDto(Post p) => new(p.Id, p.SourceId,
        new(p.Author.Id, p.Author.SourceId, p.Author.Username, p.Author.DisplayName), p.Text,
        p.PublishedAt, p.Url, p.Photos.Where(i => i.Downloaded).OrderBy(i => i.Position)
            .Select(i => new PhotoDto(i.Id, i.Position, $"/api/photos/{i.Id}")).ToArray());
}
