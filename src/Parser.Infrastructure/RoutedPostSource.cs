using Parser.Core;

namespace Parser.Infrastructure;

public sealed class RoutedPostSource(PlaywrightPostSource x, TelegramPostSource telegram) : IPostSource
{
    public IAsyncEnumerable<SourcePage> ReadAsync(SourceRequest request, CancellationToken cancellationToken) =>
        request.Source switch
        {
            PostSourceKind.X => x.ReadAsync(request, cancellationToken),
            PostSourceKind.Telegram => telegram.ReadAsync(request, cancellationToken),
            _ => throw new SourceException("invalid_source", "Неизвестный источник постов.")
        };
}
