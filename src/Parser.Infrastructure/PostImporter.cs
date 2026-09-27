using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Parser.Core;

namespace Parser.Infrastructure;

public enum ImportResult { Saved, Existing, PendingPhotos }

public sealed class PostImporter(ParserDbContext db, IOptions<ParserOptions> options)
{
    public async Task<ImportResult> ImportAsync(SourcePost source, CancellationToken cancellationToken)
    {
        var post = await db.Posts.Include(p => p.Photos).SingleOrDefaultAsync(
            p => p.Source == source.Source && p.SourceId == source.Id, cancellationToken);
        if (post?.Status == PostStatus.Ready) return ImportResult.Existing;
        if (post is null)
        {
            var author = await db.Authors.SingleOrDefaultAsync(
                a => a.Source == source.Source && a.SourceId == source.AuthorId, cancellationToken);
            if (author is null)
            {
                author = new Author { Source = source.Source, SourceId = source.AuthorId };
                db.Authors.Add(author);
            }
            author.Username = source.Username.ToLowerInvariant();
            author.DisplayName = source.DisplayName;
            post = new Post
            {
                Source = source.Source, SourceId = source.Id, Author = author, Text = source.Text,
                PublishedAt = source.PublishedAt.ToUniversalTime(),
                Url = source.Source == PostSourceKind.Telegram
                    ? source.Url ?? throw new InvalidOperationException("Telegram post URL is required.")
                    : $"https://x.com/{source.Username}/status/{source.Id}",
                ExpiresAt = options.Value.RetentionDays is int days ? DateTimeOffset.UtcNow.AddDays(days) : null
            };
            db.Posts.Add(post);
        }
        // Text is ready independently of attachments, including posts from older interrupted imports.
        post.Status = PostStatus.Ready;
        await db.SaveChangesAsync(cancellationToken);
        return ImportResult.Saved;
    }
}
