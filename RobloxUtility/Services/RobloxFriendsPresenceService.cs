using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RobloxUtility.Services;

public sealed record AccountFriendQuery(Guid AccountId, string AccountLabel, string CleanCookie);

public sealed record OnlineFriendsFetchResult(
    IReadOnlyList<OnlineFriendAggregate> Friends,
    int AccountsChecked,
    int AccountsSucceeded,
    string? WarningMessage)
{
    public bool HasUsableData => AccountsSucceeded > 0;
}

public sealed class OnlineFriendAggregate
{
    public long UserId { get; init; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int PresenceType { get; set; }
    public long? PlaceId { get; set; }
    public long? UniverseId { get; set; }
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
    private const int UniverseResolveBatchSize = 50;
    private const int PlaceDetailsBatchSize = 50;
    private static readonly TimeSpan FriendIdCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<Guid, CachedFriendIds> FriendIdCache = new();

    public static async Task<OnlineFriendsFetchResult> FetchOnlineFriendsAcrossAccountsAsync(
        IReadOnlyList<AccountFriendQuery> accounts,
        CancellationToken cancellationToken = default)
    {
        var validAccounts = accounts.Where(a => !string.IsNullOrEmpty(a.CleanCookie)).ToList();
        if (validAccounts.Count == 0)
        {
            return new OnlineFriendsFetchResult(Array.Empty<OnlineFriendAggregate>(), 0, 0, null);
        }

        var fetchTasks = validAccounts
            .Select(account => FetchAccountOnlineFriendsSafeAsync(account, cancellationToken))
            .ToList();
        var fetchResults = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var merged = new Dictionary<long, OnlineFriendAggregate>();
        var accountsSucceeded = 0;
        var accountsFailed = 0;

        foreach (var result in fetchResults)
        {
            if (result is null)
            {
                accountsFailed++;
                continue;
            }

            if (result.Value.Succeeded)
            {
                accountsSucceeded++;
            }
            else
            {
                accountsFailed++;
            }

            var (account, friends, _) = result.Value;
            foreach (var friend in friends)
            {
                MergeOnlineFriend(merged, account, friend);
            }
        }

        if (accountsSucceeded == 0)
        {
            var warning = accountsFailed == validAccounts.Count
                ? "Could not reach Roblox for any account. Wait a moment and try again."
                : "Could not load online friends.";
            return new OnlineFriendsFetchResult(Array.Empty<OnlineFriendAggregate>(), validAccounts.Count, 0, warning);
        }

        var online = merged.Values.Where(f => f.PresenceType != 0).ToList();
        if (online.Count == 0)
        {
            var warning = accountsFailed > 0
                ? $"Loaded {accountsSucceeded} account(s), but {accountsFailed} failed."
                : null;
            return new OnlineFriendsFetchResult(Array.Empty<OnlineFriendAggregate>(), validAccounts.Count, accountsSucceeded, warning);
        }

        var onlineIds = online.Select(f => f.UserId).ToList();
        var idsNeedingNames = onlineIds
            .Where(id => merged.TryGetValue(id, out var row) && NeedsUsernameResolve(row))
            .ToList();

        using var handler = CreateHandler();
        using var client = CreateClient(handler);

        await Task.WhenAll(
            idsNeedingNames.Count > 0
                ? ResolveUsernamesAsync(client, idsNeedingNames, merged, cancellationToken)
                : Task.CompletedTask,
            ResolveGameNamesAsync(client, online, cancellationToken)).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        string? partialWarning = accountsFailed > 0
            ? $"Loaded {accountsSucceeded} account(s), but {accountsFailed} failed."
            : null;

        return new OnlineFriendsFetchResult(online, validAccounts.Count, accountsSucceeded, partialWarning);
    }

    public static async Task<IReadOnlyDictionary<long, string>> FetchAvatarUrlsAsync(
        IReadOnlyList<long> userIds,
        CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        var merged = new Dictionary<long, OnlineFriendAggregate>();
        foreach (var id in userIds)
        {
            merged[id] = new OnlineFriendAggregate { UserId = id };
        }

        using var handler = CreateHandler();
        using var client = CreateClient(handler);
        await FillAvatarUrlsAsync(client, userIds, merged, cancellationToken).ConfigureAwait(false);

        return merged
            .Where(kv => !string.IsNullOrEmpty(kv.Value.AvatarUrl))
            .ToDictionary(kv => kv.Key, kv => kv.Value.AvatarUrl!);
    }

    private static async Task<(AccountFriendQuery Account, List<OnlineFriendEntry> Friends, bool Succeeded)?> FetchAccountOnlineFriendsSafeAsync(
        AccountFriendQuery account,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FetchAccountOnlineFriendsAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Line("FRIENDS", $"Could not load friends for “{account.AccountLabel}”: {ex.Message}");
            return (account, new List<OnlineFriendEntry>(), false);
        }
    }

    private static async Task<(AccountFriendQuery Account, List<OnlineFriendEntry> Friends, bool Succeeded)?> FetchAccountOnlineFriendsAsync(
        AccountFriendQuery account,
        CancellationToken cancellationToken)
    {
        using var handler = CreateHandler();
        using var client = CreateClient(handler);

        var userId = await GetAuthenticatedUserIdAsync(client, account.CleanCookie, cancellationToken).ConfigureAwait(false);
        if (userId is null)
        {
            AppLog.Line("FRIENDS", $"Skipping “{account.AccountLabel}”: cookie invalid or expired.");
            return (account, new List<OnlineFriendEntry>(), false);
        }

        var friends = await GetOnlineFriendsWithFallbackAsync(client, account, userId.Value, cancellationToken)
            .ConfigureAwait(false);
        return (account, friends.Entries, friends.Succeeded);
    }

    private static void MergeOnlineFriend(
        Dictionary<long, OnlineFriendAggregate> merged,
        AccountFriendQuery account,
        OnlineFriendEntry friend)
    {
        if (!merged.TryGetValue(friend.Id, out var row))
        {
            row = new OnlineFriendAggregate
            {
                UserId = friend.Id,
                Username = friend.Name,
                DisplayName = friend.DisplayName,
                PresenceType = friend.Presence.PresenceType,
                PlaceId = friend.Presence.PlaceId,
                UniverseId = friend.Presence.UniverseId,
                GameText = BuildGameText(friend.Presence)
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

            if (IsPresenceBetter(friend.Presence, row))
            {
                row.PresenceType = friend.Presence.PresenceType;
                row.PlaceId = friend.Presence.PlaceId;
                row.UniverseId = friend.Presence.UniverseId;
                row.GameText = BuildGameText(friend.Presence);
            }
        }

        if (!row.AccountLabels.Contains(account.AccountLabel, StringComparer.OrdinalIgnoreCase))
        {
            row.AccountLabels.Add(account.AccountLabel);
        }
    }

    private static bool NeedsUsernameResolve(OnlineFriendAggregate row) =>
        string.IsNullOrWhiteSpace(row.Username)
        || string.IsNullOrWhiteSpace(row.DisplayName)
        || row.Username == row.UserId.ToString();

    private static bool IsPresenceBetter(PresenceSnapshot incoming, OnlineFriendAggregate existing)
    {
        if (incoming.PresenceType == 0)
        {
            return false;
        }

        if (existing.PresenceType == 0)
        {
            return true;
        }

        var incomingScore = PresenceDetailScore(incoming);
        var existingScore = PresenceDetailScore(new PresenceSnapshot(
            existing.PresenceType,
            existing.PlaceId,
            existing.UniverseId,
            existing.GameText is "In a game" or "Online" or "Offline" or "Roblox Studio" ? null : existing.GameText));

        return incomingScore > existingScore || incoming.PresenceType > existing.PresenceType;
    }

    private static bool IsPresenceBetter(PresenceSnapshot incoming, PresenceSnapshot existing) =>
        PresenceDetailScore(incoming) > PresenceDetailScore(existing) || incoming.PresenceType > existing.PresenceType;

    private static int PresenceDetailScore(PresenceSnapshot presence)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(presence.LastLocation))
        {
            score += 4;
        }

        if (presence.UniverseId is > 0)
        {
            score += 2;
        }

        if (presence.PlaceId is > 0)
        {
            score += 1;
        }

        return score;
    }

    private static string BuildGameText(PresenceSnapshot presence) => presence.PresenceType switch
    {
        2 => string.IsNullOrWhiteSpace(presence.LastLocation) ? "In a game" : presence.LastLocation!,
        3 => "Roblox Studio",
        1 => "Online",
        _ => "Offline"
    };

    private static async Task<AccountOnlineFriendsResult> GetOnlineFriendsWithFallbackAsync(
        HttpClient client,
        AccountFriendQuery account,
        long userId,
        CancellationToken cancellationToken)
    {
        var (primaryFriends, primaryOk) = await FetchOnlineFriendsEndpointAsync(client, account.CleanCookie, userId, cancellationToken)
            .ConfigureAwait(false);

        if (primaryFriends.Count > 0)
        {
            return new AccountOnlineFriendsResult(primaryFriends, true);
        }

        if (!primaryOk)
        {
            AppLog.Line("FRIENDS", $"Online friends API failed for “{account.AccountLabel}”. Trying presence fallback…");
        }

        var fallbackFriends = await FetchOnlineViaPresenceAsync(client, account, userId, cancellationToken)
            .ConfigureAwait(false);
        if (fallbackFriends.Count > 0)
        {
            AppLog.Line("FRIENDS", $"Presence fallback found {fallbackFriends.Count} online friend(s) for “{account.AccountLabel}”.");
            return new AccountOnlineFriendsResult(fallbackFriends, true);
        }

        return new AccountOnlineFriendsResult(primaryFriends, primaryOk);
    }

    private static async Task<(List<OnlineFriendEntry> Friends, bool ApiSucceeded)> FetchOnlineFriendsEndpointAsync(
        HttpClient client,
        string cleanCookie,
        long userId,
        CancellationToken cancellationToken)
    {
        var list = new List<OnlineFriendEntry>();
        var path = $"https://friends.roblox.com/v1/users/{userId}/friends/online";
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cleanCookie}");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
        var r = await SendWithCsrfAsync(client, req, cancellationToken).ConfigureAwait(false);
        if (r is null || !r.IsSuccessStatusCode)
        {
            if (r?.StatusCode == HttpStatusCode.TooManyRequests)
            {
                AppLog.Line("FRIENDS", "Roblox rate-limited the online friends request. Wait a few seconds and refresh again.");
            }

            return (list, false);
        }

        var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return (list, false);
            }

            foreach (var item in data.EnumerateArray())
            {
                if (TryParseOnlineFriend(item, out var friend))
                {
                    list.Add(friend);
                }
            }

            return (list, true);
        }
        catch
        {
            return (list, false);
        }
    }

    private static async Task<List<OnlineFriendEntry>> FetchOnlineViaPresenceAsync(
        HttpClient client,
        AccountFriendQuery account,
        long userId,
        CancellationToken cancellationToken)
    {
        var friendIds = await GetFriendIdsAsync(client, account, userId, cancellationToken).ConfigureAwait(false);
        if (friendIds.Count == 0)
        {
            return new List<OnlineFriendEntry>();
        }

        var names = await GetFriendNamesAsync(client, account.CleanCookie, userId, cancellationToken).ConfigureAwait(false);
        var online = new List<OnlineFriendEntry>();

        for (var i = 0; i < friendIds.Count; i += PresenceBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = friendIds.Skip(i).Take(PresenceBatchSize).ToList();
            var snapshot = await FetchPresenceBatchAsync(client, account.CleanCookie, batch, cancellationToken)
                .ConfigureAwait(false);

            foreach (var (friendId, presence) in snapshot)
            {
                if (presence.PresenceType == 0)
                {
                    continue;
                }

                names.TryGetValue(friendId, out var stub);
                var name = stub?.Name ?? friendId.ToString();
                var display = stub?.DisplayName ?? name;
                online.Add(new OnlineFriendEntry(friendId, name, display, presence));
            }
        }

        return online;
    }

    private static async Task<Dictionary<long, PresenceSnapshot>> FetchPresenceBatchAsync(
        HttpClient client,
        string cleanCookie,
        List<long> userIds,
        CancellationToken cancellationToken)
    {
        var snapshot = new Dictionary<long, PresenceSnapshot>();
        if (userIds.Count == 0)
        {
            return snapshot;
        }

        var json = $"{{\"userIds\":[{string.Join(",", userIds)}]}}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users");
        req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cleanCookie}");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var r = await SendWithCsrfAsync(client, req, cancellationToken).ConfigureAwait(false);
        if (r is null || !r.IsSuccessStatusCode)
        {
            return snapshot;
        }

        var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("userPresences", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return snapshot;
            }

            foreach (var p in arr.EnumerateArray())
            {
                if (!TryGetInt64(p, "userId", out var uid))
                {
                    continue;
                }

                var incoming = ParsePresenceObject(p);
                if (!snapshot.TryGetValue(uid, out var existing) || IsPresenceBetter(incoming, existing))
                {
                    snapshot[uid] = incoming;
                }
            }
        }
        catch
        {
            // skip bad payload
        }

        return snapshot;
    }

    private static async Task<List<long>> GetFriendIdsAsync(
        HttpClient client,
        AccountFriendQuery account,
        long userId,
        CancellationToken cancellationToken)
    {
        if (FriendIdCache.TryGetValue(account.AccountId, out var cached)
            && DateTimeOffset.UtcNow - cached.CachedAt < FriendIdCacheTtl)
        {
            return cached.Ids;
        }

        var ids = new List<long>();
        string? cursor = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = string.IsNullOrEmpty(cursor)
                ? $"https://friends.roblox.com/v1/users/{userId}/friends?userSort=1&pageLimit=100"
                : $"https://friends.roblox.com/v1/users/{userId}/friends?userSort=1&pageLimit=100&cursor={Uri.EscapeDataString(cursor)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={account.CleanCookie}");
            var r = await SendWithCsrfAsync(client, req, cancellationToken).ConfigureAwait(false);
            if (r is null || !r.IsSuccessStatusCode)
            {
                break;
            }

            var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cursor = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                foreach (var item in data.EnumerateArray())
                {
                    if (TryGetInt64(item, "id", out var id))
                    {
                        ids.Add(id);
                    }
                }

                if (TryGetString(doc.RootElement, "nextPageCursor", out var next) && !string.IsNullOrWhiteSpace(next))
                {
                    cursor = next;
                }
            }
            catch
            {
                break;
            }
        }
        while (!string.IsNullOrEmpty(cursor));

        if (ids.Count > 0)
        {
            FriendIdCache[account.AccountId] = new CachedFriendIds(ids, DateTimeOffset.UtcNow);
        }

        return ids;
    }

    private static async Task<Dictionary<long, FriendStub>> GetFriendNamesAsync(
        HttpClient client,
        string cleanCookie,
        long userId,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<long, FriendStub>();
        var path = $"https://friends.roblox.com/v1/users/{userId}/friends?userSort=1&pageLimit=100";
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cleanCookie}");
        var r = await SendWithCsrfAsync(client, req, cancellationToken).ConfigureAwait(false);
        if (r is null || !r.IsSuccessStatusCode)
        {
            return names;
        }

        var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return names;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!TryGetInt64(item, "id", out var id))
                {
                    continue;
                }

                var name = ReadString(item, "name") ?? ReadString(item, "Name") ?? id.ToString();
                var display = ReadString(item, "displayName") ?? ReadString(item, "DisplayName") ?? name;
                names[id] = new FriendStub(id, name, display);
            }
        }
        catch
        {
            // fall through
        }

        return names;
    }

    private static bool TryParseOnlineFriend(JsonElement item, out OnlineFriendEntry friend)
    {
        friend = default!;
        if (!TryGetInt64(item, "id", out var id))
        {
            return false;
        }

        var name = ReadString(item, "name") ?? id.ToString();
        var display = ReadString(item, "displayName") ?? name;
        var presence = TryGetObject(item, "userPresence", "UserPresence", out var presenceEl)
            ? ParsePresenceObject(presenceEl)
            : new PresenceSnapshot(1, null, null, null);

        if (presence.PresenceType == 0)
        {
            presence = presence with { PresenceType = 1 };
        }

        friend = new OnlineFriendEntry(id, name, display, presence);
        return true;
    }

    private static bool TryGetObject(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object)
            {
                value = el;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetObject(JsonElement element, string name1, string name2, out JsonElement value) =>
        TryGetObject(element, out value, name1, name2);

    private static PresenceSnapshot ParsePresenceObject(JsonElement el)
    {
        var type = ReadPresenceType(el);
        var lastLocation = ReadString(el, "lastLocation", "LastLocation");

        long? placeId = null;
        if (TryGetInt64(el, "rootPlaceId", out var rootPlaceId) || TryGetInt64(el, "RootPlaceId", out rootPlaceId))
        {
            placeId = rootPlaceId;
        }
        else if (TryGetInt64(el, "placeId", out var placeIdValue) || TryGetInt64(el, "PlaceId", out placeIdValue))
        {
            placeId = placeIdValue;
        }

        long? universeId = null;
        if (TryGetInt64(el, "universeId", out var universeIdValue) || TryGetInt64(el, "UniverseId", out universeIdValue))
        {
            universeId = universeIdValue;
        }

        return new PresenceSnapshot(type, placeId, universeId, lastLocation);
    }

    private static int ReadPresenceType(JsonElement el)
    {
        foreach (var property in new[] { "userPresenceType", "UserPresenceType" })
        {
            if (!el.TryGetProperty(property, out var typeEl))
            {
                continue;
            }

            if (typeEl.ValueKind == JsonValueKind.Number)
            {
                return typeEl.GetInt32();
            }

            if (typeEl.ValueKind == JsonValueKind.String)
            {
                return MapPresenceType(typeEl.GetString());
            }
        }

        return 1;
    }

    private static int MapPresenceType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 1;
        }

        if (int.TryParse(value, out var numeric))
        {
            return numeric;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "offline" => 0,
            "online" => 1,
            "ingame" or "in game" or "in-game" or "in_game" => 2,
            "instudio" or "in studio" or "in-studio" or "in_studio" or "studio" => 3,
            _ => 1
        };
    }

    private static async Task ResolveGameNamesAsync(
        HttpClient client,
        List<OnlineFriendAggregate> online,
        CancellationToken cancellationToken)
    {
        var inGame = online.Where(f => f.IsInGame && NeedsGameNameResolve(f)).ToList();
        if (inGame.Count == 0)
        {
            return;
        }

        var universeIds = new HashSet<long>();
        foreach (var friend in inGame)
        {
            if (friend.UniverseId is > 0)
            {
                universeIds.Add(friend.UniverseId.Value);
            }
        }

        var placeIdsNeedingUniverse = inGame
            .Where(f => f.UniverseId is null or <= 0 && f.PlaceId is > 0)
            .Select(f => f.PlaceId!.Value)
            .Distinct()
            .ToList();

        if (placeIdsNeedingUniverse.Count > 0)
        {
            var placeToUniverse = await ResolveUniverseIdsFromPlacesAsync(client, placeIdsNeedingUniverse, cancellationToken)
                .ConfigureAwait(false);
            foreach (var friend in inGame)
            {
                if (friend.UniverseId is > 0 || friend.PlaceId is not > 0)
                {
                    continue;
                }

                if (placeToUniverse.TryGetValue(friend.PlaceId!.Value, out var resolvedUniverseId))
                {
                    friend.UniverseId = resolvedUniverseId;
                    universeIds.Add(resolvedUniverseId);
                }
            }
        }

        if (universeIds.Count == 0)
        {
            return;
        }

        var names = await FetchUniverseNamesAsync(client, universeIds, cancellationToken).ConfigureAwait(false);
        foreach (var friend in inGame)
        {
            if (friend.UniverseId is > 0 && names.TryGetValue(friend.UniverseId.Value, out var name))
            {
                friend.GameText = name;
            }
        }
    }

    private static bool NeedsGameNameResolve(OnlineFriendAggregate friend) =>
        string.IsNullOrWhiteSpace(friend.GameText) || friend.GameText == "In a game";

    private static async Task<Dictionary<long, long>> ResolveUniverseIdsFromPlacesAsync(
        HttpClient client,
        List<long> placeIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, long>();
        for (var i = 0; i < placeIds.Count; i += PlaceDetailsBatchSize)
        {
            var batch = placeIds.Skip(i).Take(PlaceDetailsBatchSize).ToList();
            var ids = string.Join(",", batch);
            var url = $"https://games.roblox.com/v1/games/multiget-place-details?placeIds={ids}";
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
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("placeId", out var placeEl) || placeEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("universeId", out var universeEl) || universeEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    map[placeEl.GetInt64()] = universeEl.GetInt64();
                }
            }
            catch
            {
                // skip bad payload
            }
        }

        return map;
    }

    private static async Task<Dictionary<long, string>> FetchUniverseNamesAsync(
        HttpClient client,
        HashSet<long> universeIds,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<long, string>();
        var ids = universeIds.ToList();
        for (var i = 0; i < ids.Count; i += UniverseResolveBatchSize)
        {
            var batch = ids.Skip(i).Take(UniverseResolveBatchSize).ToList();
            var query = string.Join(",", batch);
            var url = $"https://games.roblox.com/v1/games?universeIds={query}";
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
                    if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var name = ReadString(item, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names[idEl.GetInt64()] = name!;
                    }
                }
            }
            catch
            {
                // skip bad payload
            }
        }

        return names;
    }

    private static async Task ResolveUsernamesAsync(
        HttpClient client,
        List<long> userIds,
        Dictionary<long, OnlineFriendAggregate> merged,
        CancellationToken cancellationToken)
    {
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

    private static async Task FillAvatarUrlsAsync(
        HttpClient client,
        IReadOnlyList<long> userIds,
        Dictionary<long, OnlineFriendAggregate> merged,
        CancellationToken cancellationToken)
    {
        var batchTasks = new List<Task>();
        for (var i = 0; i < userIds.Count; i += ThumbnailBatchSize)
        {
            var batch = userIds.Skip(i).Take(ThumbnailBatchSize).ToList();
            batchTasks.Add(FillAvatarBatchAsync(client, batch, merged, cancellationToken));
        }

        await Task.WhenAll(batchTasks).ConfigureAwait(false);
    }

    private static async Task FillAvatarBatchAsync(
        HttpClient client,
        List<long> batch,
        Dictionary<long, OnlineFriendAggregate> merged,
        CancellationToken cancellationToken)
    {
        var ids = string.Join(",", batch);
        var url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={ids}&size=150x150&format=Png&isCircular=false";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var r = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode)
        {
            return;
        }

        var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return;
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

    private static bool TryGetInt64(JsonElement element, string property, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        value = el.GetInt64();
        return true;
    }

    private static bool TryGetInt64(JsonElement element, string property1, string property2, out long value)
    {
        if (TryGetInt64(element, property1, out value))
        {
            return true;
        }

        return TryGetInt64(element, property2, out value);
    }

    private static bool TryGetString(JsonElement element, string property, out string? value)
    {
        value = ReadString(element, property);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? ReadString(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = ReadString(element, property);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
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
            cancellationToken.ThrowIfCancellationRequested();
            using var clone = await CloneRequestAsync(req).ConfigureAwait(false);
            var r = await client.SendAsync(clone, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (r.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                continue;
            }

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

    private sealed record OnlineFriendEntry(long Id, string Name, string DisplayName, PresenceSnapshot Presence);

    private sealed record PresenceSnapshot(int PresenceType, long? PlaceId, long? UniverseId, string? LastLocation);

    private sealed record AccountOnlineFriendsResult(List<OnlineFriendEntry> Entries, bool Succeeded);

    private sealed record FriendStub(long Id, string Name, string DisplayName);

    private sealed record CachedFriendIds(List<long> Ids, DateTimeOffset CachedAt);
}
