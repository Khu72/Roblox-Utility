using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RobloxUtility.Services;

public sealed record AccountFriendQuery(Guid AccountId, string AccountLabel, string CleanCookie);

public sealed class OnlineFriendAggregate
{
    public long UserId { get; init; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int PresenceType { get; set; }
    public long? PlaceId { get; set; }
    public string GameText { get; set; } = "Online";
    public string? AvatarUrl { get; set; }
    public List<string> AccountLabels { get; } = new();

    public bool IsInGame => PresenceType == 2;
    public bool IsInStudio => PresenceType == 3;
}

/// <summary>Loads online friends and in-game status across saved accounts using each account's session cookie.</summary>
public static class RobloxFriendsPresenceService
{
    private const string WebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 Edg/128.0.0.0";
    private const int PresenceBatchSize = 100;
    private const int ThumbnailBatchSize = 100;
    private const int UsersResolveBatchSize = 100;

    public static async Task<IReadOnlyList<OnlineFriendAggregate>> FetchOnlineFriendsAcrossAccountsAsync(
        IReadOnlyList<AccountFriendQuery> accounts,
        CancellationToken cancellationToken = default)
    {
        var merged = new Dictionary<long, OnlineFriendAggregate>();
        AccountFriendQuery? presenceAccount = null;

        foreach (var account in accounts)
        {
            if (string.IsNullOrEmpty(account.CleanCookie))
            {
                continue;
            }

            try
            {
                var added = await MergeAccountFriendsAsync(account, merged, cancellationToken).ConfigureAwait(false);
                if (added && presenceAccount is null)
                {
                    presenceAccount = account;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Line("FRIENDS", $"Could not load friends for “{account.AccountLabel}”: {ex.Message}");
            }
        }

        if (merged.Count == 0 || presenceAccount is null)
        {
            return Array.Empty<OnlineFriendAggregate>();
        }

        await FillPresenceAsync(presenceAccount, merged, cancellationToken).ConfigureAwait(false);

        var online = merged.Values.Where(f => f.PresenceType != 0).ToList();
        if (online.Count == 0)
        {
            return Array.Empty<OnlineFriendAggregate>();
        }

        var onlineIds = online.Select(f => f.UserId).ToList();
        await ResolveUsernamesAsync(onlineIds, merged, cancellationToken).ConfigureAwait(false);
        await FillAvatarUrlsAsync(onlineIds, merged, cancellationToken).ConfigureAwait(false);
        return online;
    }

    private static async Task ResolveUsernamesAsync(
        List<long> userIds,
        Dictionary<long, OnlineFriendAggregate> merged,
        CancellationToken cancellationToken)
    {
        using var handler = CreateHandler();
        using var client = CreateClient(handler);

        for (var i = 0; i < userIds.Count; i += UsersResolveBatchSize)
        {
            var batch = userIds.Skip(i).Take(UsersResolveBatchSize).ToList();
            var body = $"{{\"userIds\":[{string.Join(",", batch)}],\"excludeBannedUsers\":false}}";
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://users.roblox.com/v1/users")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            var r = await SendWithCsrfAsync(client, req, cancellationToken).ConfigureAwait(false);
            if (r is null || !r.IsSuccessStatusCode)
            {
                continue;
            }

            var payload = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var uid = idEl.GetInt64();
                    if (!merged.TryGetValue(uid, out var row))
                    {
                        continue;
                    }

                    var name = ReadString(item, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        row.Username = name!;
                    }

                    var display = ReadString(item, "displayName");
                    if (!string.IsNullOrWhiteSpace(display))
                    {
                        row.DisplayName = display!;
                    }
                    else if (!string.IsNullOrWhiteSpace(name))
                    {
                        row.DisplayName = name!;
                    }
                }
            }
            catch
            {
                // skip bad payload
            }
        }
    }

    private static async Task<bool> MergeAccountFriendsAsync(
        AccountFriendQuery account,
        Dictionary<long, OnlineFriendAggregate> merged,
        CancellationToken cancellationToken)
    {
        using var handler = CreateHandler();
        using var client = CreateClient(handler);

        var userId = await GetAuthenticatedUserIdAsync(client, account.CleanCookie, cancellationToken).ConfigureAwait(false);
        if (userId is null)
        {
            AppLog.Line("FRIENDS", $"Skipping “{account.AccountLabel}”: cookie invalid or expired.");
            return false;
        }

        var friends = await GetAllFriendsAsync(client, account.CleanCookie, userId.Value, cancellationToken).ConfigureAwait(false);
        if (friends.Count == 0)
        {
            return true;
        }

        foreach (var friend in friends)
        {
            if (!merged.TryGetValue(friend.Id, out var row))
            {
                row = new OnlineFriendAggregate
                {
                    UserId = friend.Id,
                    Username = friend.Name,
                    DisplayName = friend.DisplayName
                };
                merged[friend.Id] = row;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(row.Username) && !string.IsNullOrWhiteSpace(friend.Name))
                {
                    row.Username = friend.Name;
                }

                if (string.IsNullOrWhiteSpace(row.DisplayName) && !string.IsNullOrWhiteSpace(friend.DisplayName))
                {
                    row.DisplayName = friend.DisplayName;
                }
            }

            if (!row.AccountLabels.Contains(account.AccountLabel, StringComparer.OrdinalIgnoreCase))
            {
                row.AccountLabels.Add(account.AccountLabel);
            }
        }

        return true;
    }

    private static async Task FillPresenceAsync(
        AccountFriendQuery account,
        Dictionary<long, OnlineFriendAggregate> merged,
        CancellationToken cancellationToken)
    {
        using var handler = CreateHandler();
        using var client = CreateClient(handler);
        var ids = merged.Keys.ToList();

        for (var i = 0; i < ids.Count; i += PresenceBatchSize)
        {
            var batch = ids.Skip(i).Take(PresenceBatchSize).ToList();
            var json = $"{{\"userIds\":[{string.Join(",", batch)}]}}";
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users");
            req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={account.CleanCookie}");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var r = await SendWithCsrfAsync(client, req, cancellationToken).ConfigureAwait(false);
            if (r is null || !r.IsSuccessStatusCode)
            {
                continue;
            }

            var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("userPresences", out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var p in arr.EnumerateArray())
                {
                    if (!p.TryGetProperty("userId", out var uidEl) || uidEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var uid = uidEl.GetInt64();
                    if (!merged.TryGetValue(uid, out var row))
                    {
                        continue;
                    }

                    var type = p.TryGetProperty("userPresenceType", out var typeEl) && typeEl.ValueKind == JsonValueKind.Number
                        ? typeEl.GetInt32()
                        : 0;
                    var lastLocation = p.TryGetProperty("lastLocation", out var locEl) && locEl.ValueKind == JsonValueKind.String
                        ? locEl.GetString()
                        : null;

                    long? placeId = null;
                    if (p.TryGetProperty("rootPlaceId", out var rpEl) && rpEl.ValueKind == JsonValueKind.Number)
                    {
                        placeId = rpEl.GetInt64();
                    }
                    else if (p.TryGetProperty("placeId", out var pEl) && pEl.ValueKind == JsonValueKind.Number)
                    {
                        placeId = pEl.GetInt64();
                    }

                    row.PresenceType = type;
                    row.PlaceId = placeId;
                    row.GameText = type switch
                    {
                        2 => string.IsNullOrWhiteSpace(lastLocation) ? "In a game" : lastLocation!,
                        3 => "Roblox Studio",
                        1 => "Online",
                        _ => "Offline"
                    };
                }
            }
            catch
            {
                // skip batch
            }
        }
    }

    private static async Task FillAvatarUrlsAsync(
        List<long> userIds,
        Dictionary<long, OnlineFriendAggregate> merged,
        CancellationToken cancellationToken)
    {
        using var handler = CreateHandler();
        using var client = CreateClient(handler);

        for (var i = 0; i < userIds.Count; i += ThumbnailBatchSize)
        {
            var batch = userIds.Skip(i).Take(ThumbnailBatchSize).ToList();
            var ids = string.Join(",", batch);
            var url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={ids}&size=150x150&format=Png&isCircular=false";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var r = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                continue;
            }

            var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("targetId", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var uid = idEl.GetInt64();
                    if (!merged.TryGetValue(uid, out var row))
                    {
                        continue;
                    }

                    if (item.TryGetProperty("imageUrl", out var imgEl) && imgEl.ValueKind == JsonValueKind.String)
                    {
                        var imageUrl = imgEl.GetString();
                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            row.AvatarUrl = imageUrl;
                        }
                    }
                }
            }
            catch
            {
                // skip bad thumbnail payload
            }
        }
    }

    private static async Task<long?> GetAuthenticatedUserIdAsync(HttpClient client, string cleanCookie, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated");
        req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cleanCookie}");
        var r = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || !r.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
            {
                return idEl.GetInt64();
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }

    private static async Task<List<FriendStub>> GetAllFriendsAsync(
        HttpClient client,
        string cleanCookie,
        long userId,
        CancellationToken cancellationToken)
    {
        var list = new List<FriendStub>();
        var path = $"https://friends.roblox.com/v1/users/{userId}/friends?userSort=1";
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cleanCookie}");
        var r = await SendWithCsrfAsync(client, req, cancellationToken).ConfigureAwait(false);
        if (r is null || !r.IsSuccessStatusCode)
        {
            return list;
        }

        var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var id = idEl.GetInt64();
                var name = ReadString(item, "name") ?? ReadString(item, "Name") ?? id.ToString();
                var display = ReadString(item, "displayName") ?? ReadString(item, "DisplayName") ?? name;
                list.Add(new FriendStub(id, name, display));
            }
        }
        catch
        {
            // fall through with whatever we collected
        }

        return list;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static async Task<HttpResponseMessage?> SendWithCsrfAsync(
        HttpClient client,
        HttpRequestMessage req,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var clone = await CloneRequestAsync(req).ConfigureAwait(false);
            var r = await client.SendAsync(clone, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                && r.Headers.TryGetValues("x-csrf-token", out var toks))
            {
                var t = toks.FirstOrDefault();
                if (!string.IsNullOrEmpty(t))
                {
                    if (client.DefaultRequestHeaders.Contains("X-CSRF-TOKEN"))
                    {
                        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
                    }

                    client.DefaultRequestHeaders.TryAddWithoutValidation("X-CSRF-TOKEN", t);
                }

                continue;
            }

            return r;
        }

        return null;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        foreach (var h in req.Headers)
        {
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        if (req.Content is not null)
        {
            var bytes = await req.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var media = req.Content.Headers.ContentType?.ToString() ?? "application/json";
            clone.Content = new ByteArrayContent(bytes);
            clone.Content.Headers.TryAddWithoutValidation("Content-Type", media);
        }

        return clone;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            Timeout = TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(WebUserAgent);
        return client;
    }

    private sealed record FriendStub(long Id, string Name, string DisplayName);
}
