using Microsoft.Extensions.Options;

namespace Parser.Infrastructure;

public interface IPhotoStore
{
    Task<string> DownloadAsync(string sourceUrl, string storageKey, CancellationToken cancellationToken);
    Stream OpenRead(string storageKey);
    void Delete(string storageKey);
}

public sealed class PhotoStore(HttpClient client, IOptions<ParserOptions> options) : IPhotoStore
{
    public static bool IsAllowedSource(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Port == 443 && uri.Host == "pbs.twimg.com"
        && uri.AbsolutePath.StartsWith("/media/", StringComparison.Ordinal) && uri.UserInfo.Length == 0;

    public string Resolve(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new ArgumentException("Invalid photo key.", nameof(key));
        var folder = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "photos");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, key);
    }

    public async Task<string> DownloadAsync(string sourceUrl, string storageKey, CancellationToken cancellationToken)
    {
        if (!IsAllowedSource(sourceUrl)) throw new InvalidOperationException("Unexpected photo origin.");
        var target = Resolve(storageKey);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var maxBytes = (long)options.Value.MaxPhotoMegabytes * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("Photo exceeds size limit.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var header = new byte[12];
            await input.ReadExactlyAsync(header, cancellationToken);
            var contentType = DetectType(header) ?? throw new IOException("Unsupported photo format.");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await output.WriteAsync(header, cancellationToken);
                var buffer = new byte[81920];
                long size = header.Length;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += count;
                    if (size > maxBytes) throw new IOException("Photo exceeds size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }
            File.Move(temporary, target, true);
            return contentType;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Stream OpenRead(string storageKey) => File.OpenRead(Resolve(storageKey));
    public void Delete(string storageKey) => File.Delete(Resolve(storageKey));

    public static string? DetectType(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12) return null;
        if (header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff) return "image/jpeg";
        if (header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
