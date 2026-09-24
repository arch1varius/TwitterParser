using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Parser.Contracts;

namespace Parser.WinUI;

public sealed class ApiClient : IDisposable
{
    private HttpClient client = new() { BaseAddress = new Uri("http://localhost:5080/"), Timeout = TimeSpan.FromSeconds(30) };

    public void Connect(string address, string key)
    {
        if (!Uri.TryCreate(address.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")) throw new InvalidOperationException("Укажите HTTP(S)-адрес сервера.");
        var next = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(key)) next.DefaultRequestHeaders.Add("X-Api-Key", key.Trim());
        client.Dispose(); client = next;
    }

    public async Task HealthAsync() { using var result = await client.GetAsync("health"); await CheckAsync(result); }
    public Task<List<AuthorDto>> AuthorsAsync() => GetAsync<List<AuthorDto>>("api/authors");
    public Task<ParseJobDto> JobAsync(Guid id) => GetAsync<ParseJobDto>($"api/parse-jobs/{id}");
    public Task<ParseJobLogPage> JobLogsAsync(Guid id, int page, string? reason) =>
        GetAsync<ParseJobLogPage>($"api/parse-jobs/{id}/logs?page={page}&pageSize=50&reason={Uri.EscapeDataString(reason ?? "")}");
    public Task<PostPage> PostsAsync(Guid author, DateTimeOffset from, DateTimeOffset to, int page) =>
        GetAsync<PostPage>($"api/posts?{Query(author, from, to)}&page={page}&pageSize=20");
    public Task<PostDto> RandomAsync(Guid author, DateTimeOffset from, DateTimeOffset to) =>
        GetAsync<PostDto>($"api/posts/random?{Query(author, from, to)}");
    public Task<byte[]> PhotoAsync(string path) => client.GetByteArrayAsync(path);

    public async Task<ParseJobDto> StartAsync(CreateParseJobRequest request)
    {
        using var result = await client.PostAsJsonAsync("api/parse-jobs", request);
        await CheckAsync(result);
        return (await result.Content.ReadFromJsonAsync<ParseJobDto>())!;
    }
    public async Task CancelAsync(Guid id)
    {
        using var result = await client.PostAsync($"api/parse-jobs/{id}/cancel", null);
        await CheckAsync(result);
    }
    private async Task<T> GetAsync<T>(string path)
    {
        using var result = await client.GetAsync(path);
        await CheckAsync(result);
        return (await result.Content.ReadFromJsonAsync<T>())!;
    }
    private static string Query(Guid author, DateTimeOffset from, DateTimeOffset to) =>
        $"authorId={author}&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
    private static async Task CheckAsync(HttpResponseMessage result)
    {
        if (result.IsSuccessStatusCode) return;
        string? detail = null;
        try
        {
            using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            if (json.RootElement.TryGetProperty("detail", out var value)) detail = value.GetString();
        }
        catch (JsonException) { }
        throw new HttpRequestException(detail ?? $"Сервер вернул HTTP {(int)result.StatusCode}.");
    }
    public void Dispose() => client.Dispose();
}
