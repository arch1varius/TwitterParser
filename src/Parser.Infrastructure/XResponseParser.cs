using System.Globalization;
using System.Net;
using System.Text.Json;
using Parser.Core;

namespace Parser.Infrastructure;

// Reads responses produced by the real X page; it never calls private endpoints itself.
// The upstream schema is not a public contract. Unknown/incomplete records are rejected.
public static class XResponseParser
{
    public static IReadOnlyList<SourcePost> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var posts = new Dictionary<string, SourcePost>();
        // GraphQL may return useful timeline data alongside errors for unavailable items.
        Visit(At(document.RootElement, "data"), posts);
        var errors = At(document.RootElement, "errors");
        if (posts.Count == 0 && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var codes = errors.EnumerateArray().Select(e => At(e, "code"))
                .Where(c => c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out _))
                .Select(c => c.GetInt32()).Distinct().Take(10).ToArray();
            throw new SourceException("source_api_error", "X вернул ошибку в JSON-ответе ленты"
                + (codes.Length == 0 ? "." : $" (коды: {string.Join(", ", codes)})."));
        }
        return posts.Values.ToArray();
    }

    private static void Visit(JsonElement node, Dictionary<string, SourcePost> posts)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var legacy = At(node, "legacy");
            if (At(legacy, "full_text").ValueKind == JsonValueKind.String)
            {
                var post = Read(node, legacy);
                if (post is not null) posts[post.Id] = post;
                return; // Do not import embedded quotes/reposts as independent timeline entries.
            }
            foreach (var property in node.EnumerateObject()) Visit(property.Value, posts);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) Visit(item, posts);
    }

    private static SourcePost? Read(JsonElement node, JsonElement legacy)
    {
        var id = Text(node, "rest_id");
        var created = Text(legacy, "created_at");
        if (id.Length == 0 || !DateTimeOffset.TryParseExact(created,
            "ddd MMM dd HH:mm:ss zzz yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var published))
        {
            // X uses +0000 rather than +00:00 in its legacy date representation.
            var pieces = created.Split(' ');
            if (pieces.Length == 6 && pieces[4].Length == 5)
                pieces[4] = pieces[4].Insert(3, ":");
            if (id.Length == 0 || !DateTimeOffset.TryParseExact(string.Join(' ', pieces),
                "ddd MMM dd HH:mm:ss zzz yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out published)) return null;
        }

        var user = At(node, "core", "user_results", "result");
        var username = Text(user, "core", "screen_name");
        if (username.Length == 0) username = Text(user, "legacy", "screen_name");
        var name = Text(user, "core", "name");
        if (name.Length == 0) name = Text(user, "legacy", "name");
        var text = Text(legacy, "full_text");
        var note = Text(node, "note_tweet", "note_tweet_results", "result", "text");
        if (note.Length > 0) text = note;
        var complete = (!Flag(legacy, "truncated") || note.Length > 0)
            && (At(node, "note_tweet").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || note.Length > 0)
            && At(legacy, "entities").ValueKind == JsonValueKind.Object;

        var media = At(legacy, "extended_entities", "media");
        var basicMedia = At(legacy, "entities", "media");
        if (basicMedia.ValueKind == JsonValueKind.Array && basicMedia.GetArrayLength() > 0
            && media.ValueKind != JsonValueKind.Array) complete = false;
        var photos = new List<SourcePhoto>();
        var video = false;
        if (media.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in media.EnumerateArray())
            {
                var type = Text(item, "type");
                if (type is "video" or "animated_gif") { video = true; continue; }
                if (type != "photo") { complete = false; continue; }
                var url = Text(item, "media_url_https");
                if (!PhotoStore.IsAllowedSource(url)) { complete = false; continue; }
                photos.Add(new SourcePhoto(url, Number(item, "original_info", "width"),
                    Number(item, "original_info", "height")));
            }
        }
        var card = Text(node, "card", "legacy", "name");
        if (card.Contains("video", StringComparison.OrdinalIgnoreCase)
            || card.Contains("player", StringComparison.OrdinalIgnoreCase)) video = true;
        var post = new SourcePost(id, Text(user, "rest_id"), username, WebUtility.HtmlDecode(name),
            WebUtility.HtmlDecode(text), published.ToUniversalTime(), video,
            Text(legacy, "in_reply_to_status_id_str").Length > 0,
            At(legacy, "retweeted_status_result").ValueKind == JsonValueKind.Object
                || At(node, "retweeted_status_result").ValueKind == JsonValueKind.Object
                || Text(legacy, "retweeted_status_id_str").Length > 0,
            Flag(legacy, "is_quote_status"), complete, photos);
        if (post.IsRepost)
        {
            var original = At(legacy, "retweeted_status_result", "result");
            if (original.ValueKind != JsonValueKind.Object) original = At(node, "retweeted_status_result", "result");
            if (At(original, "tweet").ValueKind == JsonValueKind.Object) original = At(original, "tweet");
            var originalLegacy = At(original, "legacy");
            var originalPost = At(originalLegacy, "full_text").ValueKind == JsonValueKind.String
                ? Read(original, originalLegacy) : null;
            // Keep the repost's own ID, owner and timestamp, but not its shortened RT preview.
            post = originalPost is null ? post with { IsComplete = false } : post with
            {
                Text = originalPost.Text, Photos = originalPost.Photos,
                HasVideo = originalPost.HasVideo, IsComplete = originalPost.IsComplete
            };
        }
        return post;
    }

    private static JsonElement At(JsonElement node, params string[] path)
    {
        foreach (var key in path)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out node)) return default;
        }
        return node;
    }
    private static string Text(JsonElement node, params string[] path)
    {
        var value = At(node, path);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    }
    private static bool Flag(JsonElement node, string key) => At(node, key).ValueKind == JsonValueKind.True;
    private static int Number(JsonElement node, params string[] path) => At(node, path).TryGetSafeInt();
    private static int TryGetSafeInt(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n) ? n : 0;
}
