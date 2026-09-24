using Microsoft.EntityFrameworkCore;
using Parser.Core;

namespace Parser.Infrastructure;

public sealed class ParserDbContext(DbContextOptions<ParserDbContext> options) : DbContext(options)
{
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostPhoto> Photos => Set<PostPhoto>();
    public DbSet<ParseJob> Jobs => Set<ParseJob>();
    public DbSet<ParseJobLog> JobLogs => Set<ParseJobLog>();
    public DbSet<FileDeletion> FileDeletions => Set<FileDeletion>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Author>(e =>
        {
            e.HasIndex(x => x.SourceId).IsUnique();
            e.HasIndex(x => x.Username);
            e.Property(x => x.SourceId).HasMaxLength(32);
            e.Property(x => x.Username).HasMaxLength(15);
            e.Property(x => x.DisplayName).HasMaxLength(200);
        });
        model.Entity<Post>(e =>
        {
            e.HasIndex(x => x.SourceId).IsUnique();
            e.HasIndex(x => new { x.AuthorId, x.PublishedAt });
            e.HasIndex(x => x.ExpiresAt);
            e.Property(x => x.SourceId).HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>();
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId);
        });
        model.Entity<PostPhoto>(e =>
        {
            e.HasIndex(x => new { x.PostId, x.Position }).IsUnique();
            e.HasOne(x => x.Post).WithMany(x => x.Photos).HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<ParseJob>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.Property(x => x.Author).HasMaxLength(15);
        });
        model.Entity<ParseJobLog>(e =>
        {
            e.Property(x => x.Reason).HasConversion<string>();
            e.Property(x => x.TextPreview).HasMaxLength(500);
            e.HasIndex(x => new { x.JobId, x.Id });
            e.HasIndex(x => new { x.JobId, x.Reason, x.Id });
            e.HasOne<ParseJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
