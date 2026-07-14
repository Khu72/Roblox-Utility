using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RobloxUtility.Services;

public static class ConsoleCommandService
{
    private const string WebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 Edg/128.0.0.0";
    private static readonly TimeSpan GetUserRecallWindow = TimeSpan.FromMinutes(15);

    private static string? _lastUsername;
    private static long? _lastUserId;
    private static DateTimeOffset? _lastUserRetrievedAt;

    public static async Task ExecuteAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (!input.StartsWith('/'))
        {
            AppLog.Warn("Commands must start with /. Try /getuser <username or user id>");
            return;
        }

        var space = input.IndexOf(' ');
        var cmd = (space < 0 ? input : input[..space]).ToLowerInvariant();
        var arg = space < 0 ? "" : input[(space + 1)..].Trim();

        switch (cmd)
        {
            case "/getuser":
                await GetUserAsync(arg, cancellationToken).ConfigureAwait(false);
                break;
            case "/strike":
                Strike(arg);
                break;
            default:
                AppLog.Warn($"Unknown command: {cmd}");
                break;
        }
    }

    private static async Task GetUserAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            AppLog.Warn("Usage: /getuser <username or user id>");
            return;
        }

        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        using var client = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version20, Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(WebUserAgent);

        string username;
        long userId;

        if (long.TryParse(query, out var parsedId) && parsedId > 0)
        {
            var resolved = await ResolveByUserIdAsync(client, parsedId, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                AppLog.Warn($"User not found: {query}");
                return;
            }

            username = resolved.Value.Username;
            userId = resolved.Value.UserId;
        }
        else
        {
            var resolved = await ResolveByUsernameAsync(client, query, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                AppLog.Warn($"User not found: {query}");
                return;
            }

            username = resolved.Value.Username;
            userId = resolved.Value.UserId;
        }

        var profileLink = $"https://www.roblox.com/users/{userId}/profile";
        RememberRetrievedUser(username, userId);
        AppLog.Ok($"{username} | {userId}");
        AppLog.Link("OK", profileLink);
    }

    private static void RememberRetrievedUser(string username, long userId)
    {
        _lastUsername = username;
        _lastUserId = userId;
        _lastUserRetrievedAt = DateTimeOffset.Now;
    }

    private static bool TryGetRecentUser(out string username, out long userId)
    {
        username = _lastUsername ?? "";
        userId = _lastUserId ?? 0;

        if (_lastUserRetrievedAt is null || string.IsNullOrEmpty(_lastUsername) || _lastUserId is null or <= 0)
        {
            return false;
        }

        if (DateTimeOffset.Now - _lastUserRetrievedAt.Value > GetUserRecallWindow)
        {
            return false;
        }

        return true;
    }

    private static void Strike(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            AppLog.Warn("Usage: /strike <reason>");
            return;
        }

        if (!TryGetRecentUser(out var username, out var userId))
        {
            AppLog.Warn("Run /getuser first. /strike only works for a user retrieved in the last 15 minutes.");
            return;
        }

        var text = $"User: {username}\r\nID: {userId}\r\nReason: {reason}\r\nStrikes: ";
        try
        {
            System.Windows.Clipboard.SetText(text);
            AppLog.Ok($"Copied strike template for {username}.");
        }
        catch (Exception ex)
        {
            AppLog.Err($"Failed to copy to clipboard: {ex.Message}");
        }
    }

    private static async Task<(string Username, long UserId)?> ResolveByUserIdAsync(
        HttpClient client,
        long userId,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://users.roblox.com/v1/users/{userId}");
        var r = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!r.IsSuccessStatusCode)
        {
            AppLog.Err($"Roblox returned {(int)r.StatusCode} while looking up user id {userId}.");
            return null;
        }

        var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseUserPayload(body);
    }

    private static async Task<(string Username, long UserId)?> ResolveByUsernameAsync(
        HttpClient client,
        string username,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            usernames = new[] { username },
            excludeBannedUsers = false
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        var r = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode)
        {
            AppLog.Err($"Roblox returned {(int)r.StatusCode} while looking up username \"{username}\".");
            return null;
        }

        var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in data.EnumerateArray())
            {
                var parsed = ParseUserElement(item);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static (string Username, long UserId)? ParseUserPayload(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ParseUserElement(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static (string Username, long UserId)? ParseUserElement(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var userId = idEl.GetInt64();
        if (userId <= 0)
        {
            return null;
        }

        var username = element.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        return (username, userId);
    }
}
