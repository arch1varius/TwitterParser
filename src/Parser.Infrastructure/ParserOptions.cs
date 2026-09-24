namespace Parser.Infrastructure;

public sealed class ParserOptions
{
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwitterParser");
    public string? StorageStatePath { get; set; }
    public string? AuthToken { get; set; }
    public string? CsrfToken { get; set; }
    public bool Headless { get; set; } = true;
    public int MaxScrolls { get; set; } = 100;
    public int ScrollDelayMs { get; set; } = 2000;
    public int MaxJobMinutes { get; set; } = 20;
    public int? RetentionDays { get; set; }
    public int MaxPhotoMegabytes { get; set; } = 20;
    public string AuthenticationPath => StorageStatePath ?? Path.Combine(DataDirectory, "storage-state.json");
}
