using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Parser.Core;
using Parser.Infrastructure;

namespace Parser.Api;

public sealed class ParseWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    IOptions<ParserOptions> options, ILogger<ParseWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A session advisory lock elects exactly one worker across server instances.
                var leaseConnection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Parser")) { Pooling = false };
                await using var lease = new NpgsqlConnection(leaseConnection.ConnectionString);
                await lease.OpenAsync(stoppingToken);
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(839214607)", lease);
                if (!(bool)(await command.ExecuteScalarAsync(stoppingToken))!)
                { await Task.Delay(3000, stoppingToken); continue; }
                await RecoverAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await using var heartbeat = new NpgsqlCommand("SELECT 1", lease);
                    await heartbeat.ExecuteScalarAsync(stoppingToken);
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ParserDbContext>();
                    await scope.ServiceProvider.GetRequiredService<RetentionService>().CleanAsync(stoppingToken);
                    var job = await db.Jobs.Where(j => j.Status == JobStatus.Queued)
                        .OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(stoppingToken);
                    if (job is null) { await Task.Delay(1000, stoppingToken); continue; }
                    await RunAsync(job, scope.ServiceProvider, lease, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker unavailable; retrying.");
                try { await Task.Delay(5000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParserDbContext>();
        await db.Jobs.Where(j => j.Status == JobStatus.Running).ExecuteUpdateAsync(s =>
            s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.StopReason, "Restarted"), ct);
        await db.Jobs.Where(j => j.Status == JobStatus.CancelRequested).ExecuteUpdateAsync(s =>
            s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.FinishedAt, DateTimeOffset.UtcNow), ct);
    }

    private async Task RunAsync(ParseJob job, IServiceProvider services, NpgsqlConnection lease, CancellationToken stoppingToken)
    {
        var db = services.GetRequiredService<ParserDbContext>();
        // Do not resurrect a job cancelled between queue selection and claim.
        var claimed = await db.Jobs.Where(j => j.Id == job.Id && j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running), stoppingToken);
        if (claimed == 0) return;
        await db.Entry(job).ReloadAsync(stoppingToken);
        job.StartedAt = DateTimeOffset.UtcNow;
        job.FinishedAt = null;
        job.Scanned = job.Saved = job.Existing = job.SkippedVideo = job.SkippedOther = job.Errors = 0;
        job.ErrorCode = job.ErrorMessage = job.StopReason = null;
        job.EarliestSeenAt = null;
        await using (var reset = await db.Database.BeginTransactionAsync(stoppingToken))
        {
            // A recovered job starts a new pass; logs and counters describe the same pass.
            await db.JobLogs.Where(entry => entry.JobId == job.Id).ExecuteDeleteAsync(stoppingToken);
            await db.SaveChangesAsync(stoppingToken);
            await reset.CommitAsync(stoppingToken);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(options.Value.MaxJobMinutes));
        using var jobToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        using var monitorToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = WatchCancellationAsync(job.Id, lease, jobToken, monitorToken.Token);
        var terminal = JobStatus.Completed;
        try
        {
            var source = services.GetRequiredService<IPostSource>();
            var importer = services.GetRequiredService<PostImporter>();
            var request = new SourceRequest(job.Author, job.From, job.To);
            var seen = new HashSet<string>();
            await foreach (var page in source.ReadAsync(request, jobToken.Token))
            {
                foreach (var post in page.Posts)
                {
                    jobToken.Token.ThrowIfCancellationRequested();
                    if (!seen.Add(post.Id)) continue;
                    job.Scanned++;
                    if (string.Equals(post.Username, job.Author, StringComparison.OrdinalIgnoreCase)
                        && (job.EarliestSeenAt is null || post.PublishedAt < job.EarliestSeenAt))
                        job.EarliestSeenAt = post.PublishedAt;
                    var filter = PostRules.Evaluate(post, request);
                    ParseJobLog? log = null;
                    if (filter != FilterResult.Accept)
                    {
                        log = PostRules.CreateLog(job.Id, post, request, filter);
                        db.JobLogs.Add(log);
                    }
                    if (filter == FilterResult.Incomplete) job.Errors++;
                    else if (filter != FilterResult.Accept) job.SkippedOther++;
                    else
                    {
                        var result = await importer.ImportAsync(post, jobToken.Token);
                        if (result == ImportResult.Saved) job.Saved++;
                        else if (result == ImportResult.Existing) job.Existing++;
                        else job.Errors++;
                    }
                    await db.SaveChangesAsync(jobToken.Token);
                    if (log is not null) db.Entry(log).State = EntityState.Detached;
                }
                if (page.StopReason is not null) job.StopReason = page.StopReason;
                await db.SaveChangesAsync(jobToken.Token);
            }
            job.StopReason ??= "SourceFinished";
        }
        catch (Exception) when (jobToken.IsCancellationRequested)
        {
            if (stoppingToken.IsCancellationRequested) return; // Next owner recovers this Running job.
            terminal = timeout.IsCancellationRequested ? JobStatus.Failed : JobStatus.Cancelled;
            job.ErrorCode = timeout.IsCancellationRequested ? "job_timeout" : "cancelled";
            job.ErrorMessage = timeout.IsCancellationRequested ? "Достигнут лимит времени задания." : "Задание отменено.";
        }
        catch (SourceException ex)
        {
            terminal = ex.Code == "login_required" ? JobStatus.NeedsLogin : JobStatus.Failed;
            job.ErrorCode = ex.Code;
            job.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            terminal = JobStatus.Failed;
            job.ErrorCode = "processing_failed";
            job.ErrorMessage = "Ошибка обработки. Подробности в журнале сервера.";
            logger.LogError(ex, "Job {JobId} failed.", job.Id);
        }
        finally
        {
            await monitorToken.CancelAsync();
            try { await monitor; } catch (OperationCanceledException) { }
        }
        await db.SaveChangesAsync(stoppingToken);
        await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s =>
            s.SetProperty(j => j.Status, j => j.Status == JobStatus.CancelRequested ? JobStatus.Cancelled : terminal)
             .SetProperty(j => j.FinishedAt, DateTimeOffset.UtcNow), stoppingToken);
    }

    private async Task WatchCancellationAsync(Guid id, NpgsqlConnection lease,
        CancellationTokenSource jobToken, CancellationToken monitorToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, monitorToken);
                // Detect a lost advisory-lock connection while the source is awaiting I/O.
                await using var heartbeat = new NpgsqlCommand("SELECT 1", lease);
                await heartbeat.ExecuteScalarAsync(monitorToken);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ParserDbContext>();
                if (await db.Jobs.AnyAsync(j => j.Id == id && j.Status == JobStatus.CancelRequested, monitorToken))
                { await jobToken.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (monitorToken.IsCancellationRequested) { }
        catch { await jobToken.CancelAsync(); throw; }
    }
}
