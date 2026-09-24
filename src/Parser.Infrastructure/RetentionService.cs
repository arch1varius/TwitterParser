using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Parser.Infrastructure;

public sealed class RetentionService(ParserDbContext db, IPhotoStore photos, ILogger<RetentionService> logger)
{
    public async Task CleanAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await db.Posts.Include(p => p.Photos).Where(p => p.ExpiresAt <= now)
            .OrderBy(p => p.ExpiresAt).Take(100).ToListAsync(cancellationToken);
        foreach (var post in expired)
        {
            foreach (var photo in post.Photos)
                db.FileDeletions.Add(new Core.FileDeletion { StorageKey = photo.StorageKey });
            db.Posts.Remove(post);
        }
        // One transaction removes records and enqueues file deletion, even across crashes.
        await db.SaveChangesAsync(cancellationToken);
        var deletions = await db.FileDeletions.Take(500).ToListAsync(cancellationToken);
        foreach (var item in deletions)
        {
            try { photos.Delete(item.StorageKey); db.FileDeletions.Remove(item); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.LogWarning("Photo deletion {Id} deferred ({ErrorType}).", item.Id, ex.GetType().Name); }
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
