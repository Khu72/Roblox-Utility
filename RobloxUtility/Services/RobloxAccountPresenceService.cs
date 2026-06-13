using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RobloxUtility.Models;

namespace RobloxUtility.Services;

/// <summary>Resolves account online / in-game state using a saved .ROBLOSECURITY session.</summary>
public static class RobloxAccountPresenceService
{
    private const string WebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 Edg/128.0.0.0";

    public static async Task<AccountPresenceKind> QueryAsync(string cleanCookie, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(cleanCookie))
        {
            return AccountPresenceKind.NoCookie;
        }

        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        using var client = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version20, Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(WebUserAgent);

        long userId;
        using (var req = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated"))
        {
            req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cleanCookie}");
            var r = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return AccountPresenceKind.InvalidCookie;
            }

            if (!r.IsSuccessStatusCode)
            {
                return AccountPresenceKind.InvalidCookie;
            }

            var body = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                {
                    return AccountPresenceKind.InvalidCookie;
                }

                userId = idEl.GetInt64();
            }
            catch
            {
                return AccountPresenceKind.InvalidCookie;
            }
        }

        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users");
            req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cleanCookie}");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
            req.Content = new StringContent($"{{\"userIds\":[{userId}]}}", Encoding.UTF8, "application/json");

            var r = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
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

            if (!r.IsSuccessStatusCode)
            {
                return AccountPresenceKind.InvalidCookie;
            }

            var pbody = await r.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(pbody);
                if (!doc.RootElement.TryGetProperty("userPresences", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                {
                    return AccountPresenceKind.Offline;
                }

                var first = arr[0];
                if (!first.TryGetProperty("userPresenceType", out var typeEl) || typeEl.ValueKind != JsonValueKind.Number)
                {
                    return AccountPresenceKind.Offline;
                }

                var t = typeEl.GetInt32();
                return t switch
                {
                    0 => AccountPresenceKind.Offline,
                    1 => AccountPresenceKind.Online,
                    2 => AccountPresenceKind.InGame,
                    3 => AccountPresenceKind.InStudio,
                    _ => AccountPresenceKind.Offline
                };
            }
            catch
            {
                return AccountPresenceKind.InvalidCookie;
            }
        }

        return AccountPresenceKind.InvalidCookie;
    }
}
