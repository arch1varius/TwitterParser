using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Parser.Contracts;
using Windows.Storage.Streams;

namespace Parser.WinUI;

public sealed class PostCard
{
    public required string Text { get; init; }
    public required string Caption { get; init; }
    public required Uri Url { get; init; }
    public ObservableCollection<BitmapImage> Photos { get; } = [];
    public string PhotoError { get; set; } = "";
}

public sealed record LogReasonOption(string Code, string Label);
public sealed record SourceOption(string Code, string Label);
public sealed record JobLogCard(string Caption, string Reason, string Detail, string TextPreview, Uri Url);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ApiClient api = new();
    private CancellationTokenSource? polling;
    private string serverUrl = "http://localhost:5080";
    private string apiKey = "";
    private string author = "";
    private SourceOption? selectedSource;
    private string message = "Подключитесь к серверу на вкладке «Подключение».";
    private string jobId = "";
    private string jobSummary = "Задание не запущено.";
    private string pageSummary = "";
    private string logSummary = "Выберите задание и нажмите «Обновить лог».";
    private string logPageSummary = "";
    private int logPage = 1;
    private int logPages = 1;
    private int logRequest;
    private LogReasonOption? selectedLogReason;
    private int page = 1;
    private DateTimeOffset? from = DateTimeOffset.Now.AddMonths(-1);
    private DateTimeOffset? to = DateTimeOffset.Now;
    private AuthorDto? selectedAuthor;
    public string ServerUrl { get => serverUrl; set => SetProperty(ref serverUrl, value); }
    public string ApiKey { get => apiKey; set => SetProperty(ref apiKey, value); }
    public string Author { get => author; set => SetProperty(ref author, value); }
    public SourceOption? SelectedSource
    {
        get => selectedSource;
        set
        {
            if (!SetProperty(ref selectedSource, value)) return;
            OnPropertyChanged(nameof(AuthorHeader));
            OnPropertyChanged(nameof(AuthorPlaceholder));
            OnPropertyChanged(nameof(CollectionHelp));
        }
    }
    private string SourceCode => SelectedSource?.Code ?? "x";
    public string AuthorHeader => SourceCode == "telegram" ? "Канал Telegram" : "Username автора в X";
    public string AuthorPlaceholder => SourceCode == "telegram"
        ? "@channel, https://t.me/channel или ID -100…" : "username или @username";
    public string CollectionHelp => SourceCode == "telegram"
        ? "Сохраняются текст и подписи к медиа строго больше 10 слов. Вложения не скачиваются. Перед первым сбором настройте аккаунт Telegram на сервере и выполните вход по инструкции в README."
        : "Сохраняется текст постов от 10 слов, включая ответы, репосты и цитаты. Изображения, видео и GIF не скачиваются. Причины пропусков доступны на вкладке «Лог задания».";
    public string Message { get => message; set => SetProperty(ref message, value); }
    public string JobId
    {
        get => jobId;
        set { if (SetProperty(ref jobId, value)) ResetLog(); }
    }
    public string JobSummary { get => jobSummary; set => SetProperty(ref jobSummary, value); }
    public string PageSummary { get => pageSummary; set => SetProperty(ref pageSummary, value); }
    public string LogSummary { get => logSummary; set => SetProperty(ref logSummary, value); }
    public string LogPageSummary { get => logPageSummary; set => SetProperty(ref logPageSummary, value); }
    public LogReasonOption? SelectedLogReason
    {
        get => selectedLogReason;
        set { if (SetProperty(ref selectedLogReason, value)) ResetLog(); }
    }
    public DateTimeOffset? From { get => from; set => SetProperty(ref from, value); }
    public DateTimeOffset? To { get => to; set => SetProperty(ref to, value); }
    public AuthorDto? SelectedAuthor { get => selectedAuthor; set => SetProperty(ref selectedAuthor, value); }
    public string TimeZoneLabel => $"Даты включительно · {TimeZoneInfo.Local.DisplayName}";
    public ObservableCollection<AuthorDto> Authors { get; } = [];
    public ObservableCollection<PostCard> Posts { get; } = [];
    public ObservableCollection<PostCard> RandomPosts { get; } = [];
    public ObservableCollection<JobLogCard> JobLogs { get; } = [];
    public IReadOnlyList<SourceOption> Sources { get; } = [new("x", "X"), new("telegram", "Telegram")];
    public IReadOnlyList<LogReasonOption> LogReasons { get; } =
    [
        new("", "Все причины"), new("WrongAuthor", "Другой автор"),
        new("OutsidePeriod", "Вне периода"), new("TooShort", "Недостаточно слов"),
        new("Incomplete", "Неполные данные (ошибка)")
    ];
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand RefreshAuthorsCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand TrackCommand { get; }
    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand RandomCommand { get; }
    public IAsyncRelayCommand LoadLogCommand { get; }
    public IAsyncRelayCommand NextLogCommand { get; }
    public IAsyncRelayCommand PreviousLogCommand { get; }

    public MainViewModel()
    {
        selectedSource = Sources[0];
        ConnectCommand = Command(async () =>
        {
            polling?.Cancel(); ResetLog(); api.Connect(ServerUrl, ApiKey);
            await api.HealthAsync(); await RefreshAuthorsAsync(); Message = "Соединение установлено.";
        });
        RefreshAuthorsCommand = Command(RefreshAuthorsAsync);
        StartCommand = Command(async () =>
        {
            var (start, end) = Period();
            var job = await api.StartAsync(new(Author.Trim(), start, end, SourceCode));
            JobId = job.Id.ToString(); ShowJob(job); StartPolling(job.Id);
        });
        TrackCommand = Command(async () =>
        {
            var id = Guid.Parse(JobId); ShowJob(await api.JobAsync(id)); StartPolling(id);
        });
        CancelCommand = Command(async () => { await api.CancelAsync(Guid.Parse(JobId)); Message = "Отмена запрошена."; });
        LoadCommand = Command(async () => { page = 1; await LoadAsync(); });
        NextCommand = Command(async () => { page++; await LoadAsync(); });
        PreviousCommand = Command(async () => { page = Math.Max(1, page - 1); await LoadAsync(); });
        selectedLogReason = LogReasons[0];
        LoadLogCommand = Command(() => LoadLogAsync(1));
        NextLogCommand = Command(() => LoadLogAsync(Math.Min(logPage + 1, logPages)));
        PreviousLogCommand = Command(() => LoadLogAsync(Math.Max(1, logPage - 1)));
        RandomCommand = Command(async () =>
        {
            var (start, end) = Period();
            RandomPosts.Clear();
            RandomPosts.Add(await ToCardAsync(await api.RandomAsync(RequireAuthor(), start, end)));
            Message = "Получен случайный пост.";
        });
    }

    private IAsyncRelayCommand Command(Func<Task> action) => new AsyncRelayCommand(async () =>
    {
        try { await action(); }
        catch (Exception ex) { Message = ex.Message; }
    });
    private Guid RequireAuthor() => SelectedAuthor?.Id ?? throw new InvalidOperationException("Выберите сохранённого автора.");
    private (DateTimeOffset, DateTimeOffset) Period()
    {
        if (From is null || To is null || From.Value.Date > To.Value.Date)
            throw new InvalidOperationException("Выберите корректный период.");
        var start = From.Value.Date;
        var end = To.Value.Date.AddDays(1);
        return (new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)).ToUniversalTime(),
            new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end)).ToUniversalTime());
    }
    private async Task RefreshAuthorsAsync()
    {
        var id = SelectedAuthor?.Id;
        var items = await api.AuthorsAsync(); Authors.Clear();
        foreach (var item in items) Authors.Add(item);
        SelectedAuthor = Authors.FirstOrDefault(a => a.Id == id) ?? Authors.FirstOrDefault();
    }
    private async Task LoadAsync()
    {
        var (start, end) = Period();
        var result = await api.PostsAsync(RequireAuthor(), start, end, page);
        Posts.Clear();
        foreach (var post in result.Items) Posts.Add(await ToCardAsync(post));
        PageSummary = $"Страница {result.Page} · всего постов {result.Total}";
        Message = result.Total == 0 ? "Нет загруженных постов за выбранный период." : "Посты загружены.";
    }
    private async Task<PostCard> ToCardAsync(PostDto post)
    {
        var card = new PostCard { Text = post.Text, Caption = $"{SourceLabel(post.Source)} · @{post.Author.Username} · {post.PublishedAt.ToLocalTime():g}", Url = new(post.Url) };
        foreach (var photo in post.Photos)
        {
            try
            {
                var bytes = await api.PhotoAsync(photo.Url);
                using var stream = new InMemoryRandomAccessStream();
                using var writer = new DataWriter(stream);
                writer.WriteBytes(bytes); await writer.StoreAsync(); stream.Seek(0);
                var image = new BitmapImage(); await image.SetSourceAsync(stream); card.Photos.Add(image);
            }
            catch (Exception) { card.PhotoError = "Не удалось загрузить часть фотографий. Обновите выдачу."; }
        }
        return card;
    }
    private void ResetLog()
    {
        logRequest++;
        logPage = logPages = 1;
        JobLogs.Clear();
        LogPageSummary = "";
        LogSummary = "Нажмите «Обновить лог», чтобы загрузить причины пропусков выбранного задания.";
    }
    private async Task LoadLogAsync(int number)
    {
        if (!Guid.TryParse(JobId, out var id)) throw new InvalidOperationException("Укажите корректный ID задания.");
        var request = ++logRequest;
        var reason = SelectedLogReason?.Code;
        var job = await api.JobAsync(id);
        var result = await api.JobLogsAsync(id, number, reason);
        if (request != logRequest) return;
        logPages = Math.Max(1, (result.Total + result.PageSize - 1) / result.PageSize);
        if (result.Page > logPages) { await LoadLogAsync(logPages); return; }
        logPage = result.Page;
        JobLogs.Clear();
        foreach (var entry in result.Items)
        {
            var label = LogReasons.FirstOrDefault(item => item.Code == entry.Reason)?.Label ?? entry.Reason;
            JobLogs.Add(new JobLogCard(
                $"@{entry.Username} · опубликован {entry.PublishedAt.ToLocalTime():g} · {entry.WordCount} слов · ID {entry.SourceId}\nЗаписано в лог: {entry.CreatedAt.ToLocalTime():g}",
                $"{(entry.IsError ? "Ошибка" : "Пропуск")}: {label}", entry.Detail, entry.TextPreview,
                LogUrl(job, entry)));
        }
        LogSummary = result.Reasons.Count == 0
            ? "Записей пока нет. Лог создаётся при обработке постов после обновления сервера; причины старых пропусков не сохранены."
            : "По всему заданию: " + string.Join(" · ", result.Reasons.Select(item =>
                $"{LogReasons.FirstOrDefault(option => option.Code == item.Reason)?.Label ?? item.Reason}: {item.Count}"));
        LogPageSummary = $"Страница {logPage} из {logPages} · по фильтру {result.Total}";
        if (result.Total == 0 && result.Reasons.Count > 0) LogPageSummary += " · записей с этой причиной нет";
    }
    private void StartPolling(Guid id)
    {
        polling?.Cancel(); polling?.Dispose(); polling = new();
        _ = PollAsync(id, polling.Token);
    }
    private async Task PollAsync(Guid id, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var job = await api.JobAsync(id);
                token.ThrowIfCancellationRequested(); ShowJob(job);
                if (job.Status is not ("Queued" or "Running" or "CancelRequested"))
                { await RefreshAuthorsAsync(); return; }
                await Task.Delay(1500, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Message = ex.Message; }
    }
    private void ShowJob(ParseJobDto job)
    {
        JobSummary = $"{SourceLabel(job.Source)} · {job.Author} · {job.Status}\nПросмотрено: {job.Scanned} · сохранено: {job.Saved} · уже было: {job.Existing}\n"
            + $"Пропущено: {job.SkippedOther + job.SkippedVideo} · ошибок: {job.Errors}\n"
            + $"Самый ранний найденный: {job.EarliestSeenAt?.ToLocalTime().ToString("g") ?? "—"}\n"
            + $"Причина остановки: {job.StopReason ?? "—"}\n{job.ErrorMessage}";
        Message = job.Source == "telegram" ? "Статус задания Telegram обновлён."
            : "Статус задания обновлён. Полнота истории X не гарантируется.";
    }
    private static string SourceLabel(string source) => source == "telegram" ? "Telegram" : "X";
    private static Uri LogUrl(ParseJobDto job, ParseJobLogDto entry)
    {
        if (job.Source != "telegram") return new Uri($"https://x.com/i/status/{Uri.EscapeDataString(entry.SourceId)}");
        var ids = entry.SourceId.Split(':');
        return job.Author.StartsWith("-100", StringComparison.Ordinal)
            ? new Uri($"https://t.me/c/{ids[0]}/{ids[1]}")
            : new Uri($"https://t.me/{Uri.EscapeDataString(job.Author)}/{ids[1]}");
    }
    public void Dispose() { polling?.Cancel(); polling?.Dispose(); api.Dispose(); }
}
