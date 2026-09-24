namespace Parser.Contracts;

public sealed record CreateParseJobRequest(string Author, DateTimeOffset From, DateTimeOffset To);
public sealed record AuthorDto(Guid Id, string SourceId, string Username, string DisplayName);
public sealed record PhotoDto(Guid Id, int Position, string Url);
public sealed record PostDto(Guid Id, string SourceId, AuthorDto Author, string Text,
    DateTimeOffset PublishedAt, string Url, IReadOnlyList<PhotoDto> Photos);
public sealed record PostPage(IReadOnlyList<PostDto> Items, int Page, int PageSize, int Total);
public sealed record ParseJobLogDto(long Id, DateTimeOffset CreatedAt, string SourceId, string Username,
    DateTimeOffset PublishedAt, string TextPreview, int WordCount, string Reason, string Detail, bool IsError);
public sealed record ParseJobLogCount(string Reason, int Count);
public sealed record ParseJobLogPage(IReadOnlyList<ParseJobLogDto> Items, int Page, int PageSize, int Total,
    IReadOnlyList<ParseJobLogCount> Reasons);
public sealed record ParseJobDto(Guid Id, string Author, DateTimeOffset From, DateTimeOffset To,
    string Status, int Scanned, int Saved, int Existing, int SkippedVideo, int SkippedOther,
    int Errors, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    DateTimeOffset? EarliestSeenAt, string? StopReason, string? ErrorCode, string? ErrorMessage);
