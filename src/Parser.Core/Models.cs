namespace Parser.Core;

public enum JobStatus { Queued, Running, CancelRequested, Cancelled, Completed, Failed, NeedsLogin }
public enum PostStatus { PendingPhotos, Ready }
public enum PostSourceKind { X, Telegram }

public sealed class Author
{
    public PostSourceKind Source { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceId { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class Post
{
    public PostSourceKind Source { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceId { get; set; } = "";
    public Guid AuthorId { get; set; }
    public Author Author { get; set; } = null!;
    public string Text { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public PostStatus Status { get; set; }
    public List<PostPhoto> Photos { get; set; } = [];
}

public sealed class PostPhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PostId { get; set; }
    public Post Post { get; set; } = null!;
    public int Position { get; set; }
    public string SourceUrl { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Downloaded { get; set; }
}

public sealed class ParseJob
{
    public PostSourceKind Source { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Author { get; set; } = "";
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Scanned { get; set; }
    public int Saved { get; set; }
    public int Existing { get; set; }
    public int SkippedVideo { get; set; }
    public int SkippedOther { get; set; }
    public int Errors { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? EarliestSeenAt { get; set; }
    public string? StopReason { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class ParseJobLog
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string SourceId { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public string TextPreview { get; set; } = "";
    public int WordCount { get; set; }
    public FilterResult Reason { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class FileDeletion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StorageKey { get; set; } = "";
}
