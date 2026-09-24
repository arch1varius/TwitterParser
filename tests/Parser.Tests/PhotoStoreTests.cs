using System.Net;
using Microsoft.Extensions.Options;
using Parser.Infrastructure;
using Xunit;

namespace Parser.Tests;

public sealed class PhotoStoreTests
{
    [Fact]
    public async Task ActualStoreValidatesContentAndWritesAtomically()
    {
        var folder = Path.Combine(Path.GetTempPath(), "parser-photo-tests-" + Guid.NewGuid().ToString("N"));
        var handler = new ReplyHandler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(FakePhotoStore.Png) });
        using var client = new HttpClient(handler);
        var store = new PhotoStore(client, Options.Create(new ParserOptions { DataDirectory = folder }));
        var key = Guid.NewGuid().ToString("N");
        try
        {
            Assert.Equal("image/png", await store.DownloadAsync("https://pbs.twimg.com/media/a.png", key, default));
            await using (var saved = store.OpenRead(key))
            {
                using var output = new MemoryStream(); await saved.CopyToAsync(output);
                Assert.Equal(FakePhotoStore.Png, output.ToArray());
            }
            handler.Reply = () => new(HttpStatusCode.OK) { Content = new StringContent("<html>Not an image</html>") };
            await Assert.ThrowsAsync<IOException>(() => store.DownloadAsync("https://pbs.twimg.com/media/a.png", key, default));
            Assert.Single(Directory.GetFiles(Path.Combine(folder, "photos")));
            Assert.Throws<ArgumentException>(() => store.OpenRead("../../secret"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DownloadAsync("http://localhost/internal", key, default));
        }
        finally { store.Delete(key); Directory.Delete(Path.Combine(folder, "photos")); Directory.Delete(folder); }
    }

    [Fact]
    public async Task OversizedDownloadLeavesNoPartialFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "parser-photo-tests-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new ReplyHandler(() => new(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[2 * 1024 * 1024]) }));
        var store = new PhotoStore(client, Options.Create(new ParserOptions { DataDirectory = folder, MaxPhotoMegabytes = 1 }));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => store.DownloadAsync("https://pbs.twimg.com/media/a.png", Guid.NewGuid().ToString("N"), default));
            Assert.Empty(Directory.GetFiles(Path.Combine(folder, "photos")));
        }
        finally { Directory.Delete(Path.Combine(folder, "photos")); Directory.Delete(folder); }
    }

    private sealed class ReplyHandler(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Reply { get; set; } = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Reply());
    }
}
