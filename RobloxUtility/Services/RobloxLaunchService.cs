using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace RobloxUtility.Services;

/// <summary>Join games using .ROBLOSECURITY — Roblox's old www GET PlaceLauncher path often 404s; we use the auth-ticket + roblox-player + assetgame flow.</summary>
public sealed class RobloxLaunchService
{
    /// <summary>Matches Roblox's web play flow; avoids generic WinINet UA which Roblox may reject.</summary>
    private const string WebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 Edg/128.0.0.0";

    private static readonly Regex RobloxPlayerLaunchLink = new(
        "roblox-player:1[+]launch[^\"\\s<>\\r\\n]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    public static async Task<LaunchResult> LaunchWithCookieAsync(
        string robloxSecurityCookie,
        long placeId,
        long browserTrackerId = 0,
        CancellationToken cancellationToken = default)
    {
        var clean = RobloxSessionCookie.Sanitize(robloxSecurityCookie);
        if (string.IsNullOrEmpty(clean))
        {
            return new LaunchResult(false, "Save a .ROBLOSECURITY cookie for this account first (from your browser, devtools → Application → Cookies for roblox.com).");
        }

        if (placeId <= 0)
        {
            return new LaunchResult(false, "Place ID must be greater than 0.");
        }

        var tracker = browserTrackerId > 0
            ? browserTrackerId
            : Random.Shared.NextInt64(100_000_000L, int.MaxValue);

        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };
        using var client = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version20, Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(WebUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        var sessionCookie = clean;

        // 1) Try JSON join from gamejoin (works with current CSRF + session).
        var joinGame = await TryPostJoinGameAsync(client, () => sessionCookie, c => sessionCookie = c, placeId, tracker, cancellationToken);
        if (joinGame is { } j)
        {
            return WithSessionUpdates(j, sessionCookie, clean, tracker);
        }

        // 2) Try classic GET on assetgame (lowercase path; www/game often 404s).
        var fromAshx = await TryGetPlaceLauncherJsonAsync(client, () => sessionCookie, c => sessionCookie = c, placeId, cancellationToken);
        if (fromAshx is { } a)
        {
            return WithSessionUpdates(a, sessionCookie, clean, tracker);
        }

        // 3) Auth-ticket + roblox-player: (same flow as the website Play button in external tools).
        var (ok, err, tix, challenge) = await RobloxSessionCookie.GetAuthenticationTicketAsync(
            client,
            () => sessionCookie,
            c => sessionCookie = c,
            cancellationToken);
        if (challenge)
        {
            return WithSessionUpdates(
                new LaunchResult(
                    false,
                    "Roblox asked for bot verification on this account. Open roblox.com in your browser while logged into this account, complete the check, copy a fresh .ROBLOSECURITY, then Save and try again."),
                sessionCookie,
                clean,
                tracker);
        }

        if (!ok || tix is null)
        {
            return WithSessionUpdates(
                new LaunchResult(false, err ?? "Could not get an authentication ticket. Try again or paste a new .ROBLOSECURITY from an active session."),
                sessionCookie,
                clean,
                tracker);
        }

        return WithSessionUpdates(StartRobloxPlayerPlayUri(tix, placeId, tracker), sessionCookie, clean, tracker);
    }

    /// <summary>Opens the installed client with the same session (auth ticket) — no place join. Falls back to plain Roblox if ticket fails.</summary>
    public static async Task<LaunchResult> StartRobloxClientWithCookieAsync(
        string robloxSecurityCookie,
        long browserTrackerId = 0,
        CancellationToken cancellationToken = default)
    {
        var clean = RobloxSessionCookie.Sanitize(robloxSecurityCookie);
        if (string.IsNullOrEmpty(clean))
        {
            return new LaunchResult(false, "Set and save a .ROBLOSECURITY for this account first, then use Start client only again.");
        }

        var tracker = browserTrackerId > 0
            ? browserTrackerId
            : Random.Shared.NextInt64(100_000_000L, int.MaxValue);

        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };
        using var client = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version20, Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(WebUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        var sessionCookie = clean;
        var (ok, err, tix, challenge) = await RobloxSessionCookie.GetAuthenticationTicketAsync(
            client,
            () => sessionCookie,
            c => sessionCookie = c,
            cancellationToken);
        if (challenge)
        {
            return WithSessionUpdates(
                new LaunchResult(
                    false,
                    "Roblox asked for bot verification on this account. Complete it on roblox.com in your browser, then paste a fresh .ROBLOSECURITY and Save."),
                sessionCookie,
                clean,
                tracker);
        }

        if (ok && tix is not null)
        {
            var u = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // launchmode:app = desktop shell; gameinfo = session (same as web flow to signed-in app).
            var appUri = $"roblox-player:1+launchmode:app+gameinfo:{tix}+launchtime:{u}+robloxLocale:en_us+gameLocale:en_us+channel:";
            if (StartProtocolUri(appUri).Ok)
            {
                return WithSessionUpdates(
                    new LaunchResult(true, "Started the Roblox client for this account (session from saved cookie)."),
                    sessionCookie,
                    clean,
                    tracker);
            }
        }
        else if (!string.IsNullOrEmpty(err))
        {
            AppLog.Warn(err);
        }

        return WithSessionUpdates(StartRobloxClientOnly(), sessionCookie, clean, tracker);
    }

    public static LaunchResult StartRobloxClientOnly()
    {
        var path = RobloxPathHelper.FindRobloxPlayerBeta();
        if (string.IsNullOrEmpty(path))
        {
            return new LaunchResult(false, "Could not find RobloxPlayerBeta.exe. Install the Roblox client or join a game from the website once so it is installed under LocalAppData.");
        }

        return StartProcessPath(path, string.Empty);
    }

    private static LaunchResult WithSessionUpdates(
        LaunchResult result,
        string currentCookie,
        string originalCookie,
        long tracker)
    {
        string? rotated = null;
        if (!string.IsNullOrEmpty(currentCookie)
            && !string.Equals(currentCookie, originalCookie, StringComparison.Ordinal))
        {
            rotated = currentCookie;
            AppLog.Line("AUTH", "Roblox rotated .ROBLOSECURITY — saved the new cookie for this account.");
        }

        return result with
        {
            UpdatedCookie = rotated,
            BrowserTrackerId = tracker
        };
    }

    private static void ApplySetCookieUpdates(HttpResponseMessage response, Action<string> setCookie)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)
            && !(response.Content?.Headers.TryGetValues("Set-Cookie", out values) ?? false))
        {
            return;
        }

        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // ".ROBLOSECURITY=VALUE; path=/; ..."
            const string prefix = ".ROBLOSECURITY=";
            var idx = raw.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var start = idx + prefix.Length;
            var end = raw.IndexOf(';', start);
            var value = end > start ? raw[start..end] : raw[start..];
            value = RobloxSessionCookie.Sanitize(Uri.UnescapeDataString(value.Trim()));
            if (!string.IsNullOrEmpty(value))
            {
                setCookie(value);
            }
        }
    }

    private static async Task<LaunchResult?> TryPostJoinGameAsync(
        HttpClient client,
        Func<string> getCookie,
        Action<string> setCookie,
        long placeId,
        long tracker,
        CancellationToken ct)
    {
        var joinAttemptId = Guid.NewGuid().ToString();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://gamejoin.roblox.com/v1/join-game");
            req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={getCookie()}");
            req.Headers.Add("Origin", "https://www.roblox.com");
            req.Headers.Add("Referer", $"https://www.roblox.com/games/{placeId}/");
            var p = placeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var tr = tracker.ToString(System.Globalization.CultureInfo.InvariantCulture);
            req.Content = new StringContent(
                $"{{\"placeId\":{p},\"isPlayTogetherGame\":false,\"isTeleport\":false,\"browserTrackerId\":{tr},\"gameJoinAttemptId\":\"{joinAttemptId}\"}}",
                Encoding.UTF8,
                "application/json");
            var r = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            ApplySetCookieUpdates(r, setCookie);
            if (r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                && r.Headers.TryGetValues("x-csrf-token", out var csf))
            {
                var token = csf.FirstOrDefault();
                var bodyPeek = await r.Content.ReadAsStringAsync(ct);
                if (LooksLikeChallenge(r, bodyPeek))
                {
                    return new LaunchResult(
                        false,
                        "Roblox asked for bot verification before joining. Open this experience on roblox.com in your browser, complete the check, then copy a fresh .ROBLOSECURITY and Save.");
                }

                if (!string.IsNullOrEmpty(token))
                {
                    if (client.DefaultRequestHeaders.Contains("X-CSRF-TOKEN"))
                    {
                        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
                    }

                    client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
                }

                continue;
            }

            var body = await r.Content.ReadAsStringAsync(ct);
            if (LooksLikeChallenge(r, body))
            {
                return new LaunchResult(
                    false,
                    "Roblox asked for bot verification before joining. Open this experience on roblox.com in your browser, complete the check, then copy a fresh .ROBLOSECURITY and Save.");
            }

            if (!r.IsSuccessStatusCode)
            {
                return null;
            }

            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            if (FindLaunchString(body, out _) is { } link && !string.IsNullOrEmpty(link))
            {
                return StartProtocolUri(link);
            }
        }

        return null;
    }

    private static async Task<LaunchResult?> TryGetPlaceLauncherJsonAsync(
        HttpClient client,
        Func<string> getCookie,
        Action<string> setCookie,
        long placeId,
        CancellationToken ct)
    {
        // Lowercase "placelauncher" matches rbxlaunch; host assetgame serves the launcher.
        var url = $"https://assetgame.roblox.com/game/placelauncher.ashx?request=RequestGame&placeId={placeId.ToString(System.Globalization.CultureInfo.InvariantCulture)}&isPlayTogetherGame=false&gender=";
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("Origin", "https://www.roblox.com");
            req.Headers.Add("Referer", $"https://www.roblox.com/games/{placeId}/");
            req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={getCookie()}");

            var r = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            ApplySetCookieUpdates(r, setCookie);
            if (r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                && r.Headers.TryGetValues("x-csrf-token", out var csf))
            {
                var token = csf.FirstOrDefault();
                var bodyPeek = await r.Content.ReadAsStringAsync(ct);
                if (LooksLikeChallenge(r, bodyPeek))
                {
                    return new LaunchResult(
                        false,
                        "Roblox asked for bot verification before joining. Complete it on roblox.com in your browser, then paste a fresh .ROBLOSECURITY and Save.");
                }

                if (!string.IsNullOrEmpty(token))
                {
                    if (client.DefaultRequestHeaders.Contains("X-CSRF-TOKEN"))
                    {
                        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
                    }

                    client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
                }

                continue;
            }

            var body = await r.Content.ReadAsStringAsync(ct);
            if (LooksLikeChallenge(r, body))
            {
                return new LaunchResult(
                    false,
                    "Roblox asked for bot verification before joining. Complete it on roblox.com in your browser, then paste a fresh .ROBLOSECURITY and Save.");
            }

            if (!r.IsSuccessStatusCode)
            {
                return null;
            }

            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            if (FindLaunchString(body, out _) is { } link && !string.IsNullOrEmpty(link))
            {
                return StartProtocolUri(link);
            }
        }

        return null;
    }

    private static bool LooksLikeChallenge(HttpResponseMessage response, string? body)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.Contains("challenge", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        return body.Contains("Challenge is required", StringComparison.OrdinalIgnoreCase)
               || body.Contains("\"challengeType\"", StringComparison.OrdinalIgnoreCase)
               || body.Contains("rblx-challenge", StringComparison.OrdinalIgnoreCase)
               || body.Contains("Verifying you", StringComparison.OrdinalIgnoreCase);
    }

    private static LaunchResult StartRobloxPlayerPlayUri(string authTicket, long placeId, long tracker)
    {
        // Matches roblox-cmd-launcher / community format: gameinfo (ticket) + encoded placelauncherurl + +browsertrackerid+locales+channel
        var joinId = Guid.NewGuid().ToString("N");
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tr = tracker.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var plPart =
            "https%3A%2F%2Fassetgame.roblox.com%2Fgame%2FPlaceLauncher.ashx%3F" +
            "request%3DRequestGame%26" +
            $"browserTrackerId%3D{tr}%26" +
            $"placeId%3D{placeId.ToString(System.Globalization.CultureInfo.InvariantCulture)}%26" +
            "isPlayTogetherGame%3Dfalse%26" +
            $"joinAttemptId%3D{joinId}%26" +
            "joinAttemptOrigin%3DPlayButton" +
            $"+browsertrackerid:{tr}" +
            "+robloxLocale:en_us+gameLocale:en_us+channel:";

        // gameinfo: ticket must not be URL-encoded; protocol uses + as delimiter; tickets are typically safe.
        var uri = $"roblox-player:1+launchmode:play+gameinfo:{authTicket}+launchtime:{t}+placelauncherurl:{plPart}";
        return StartProtocolUri(uri);
    }

    private static LaunchResult StartProcessPath(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            });
            return new LaunchResult(true, "Started Roblox client.");
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, ex.Message);
        }
    }

    private static LaunchResult StartProtocolUri(string uri)
    {
        if (string.IsNullOrEmpty(uri) || !uri.Contains(':', StringComparison.Ordinal))
        {
            return new LaunchResult(false, "No valid launch link was produced.");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });
            return new LaunchResult(true, "Launched the game in Roblox (session-based launch was opened).");
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, $"Failed to start launch URL: {ex.Message}");
        }
    }

    private static string? FindLaunchString(string body, out string? failureReason)
    {
        failureReason = null;
        if (string.IsNullOrEmpty(body))
        {
            failureReason = "Empty response from Roblox. Try a fresh .ROBLOSECURITY from a logged-in browser session.";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("joinUrl", out var v))
            {
                return v.GetString();
            }

            if (doc.RootElement.TryGetProperty("joinScriptUrl", out v))
            {
                return v.GetString();
            }
        }
        catch
        {
            // not json
        }

        var m = RobloxPlayerLaunchLink.Match(body);
        if (m.Success)
        {
            return m.Value;
        }

        if (body.Contains("Authentication error", StringComparison.OrdinalIgnoreCase)
            || (body.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) && !body.Contains("queuePosition", StringComparison.OrdinalIgnoreCase)))
        {
            failureReason = "Roblox did not return a game launch link. The session may be invalid — copy a new .ROBLOSECURITY and Save.";
        }
        else if (body.Length < 20 && (body.Contains("not found", StringComparison.OrdinalIgnoreCase) || body.Contains("404", StringComparison.Ordinal)))
        {
            failureReason = "That place or API response was not found. Confirm the place ID is a game page ID (the number in roblox.com/games/ID/…), not the Creator Hub universe id.";
        }
        else
        {
            failureReason = "Could not read a game launch link from the response. The place may be private, full, or region-restricted, or your ID may be a universe id instead of a place id.";
        }

        return null;
    }
}

public static class RobloxSessionCookie
{
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var s = raw.Trim();
        if (s.Contains(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase))
        {
            var i = s.IndexOf(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase);
            s = s[(i + ".ROBLOSECURITY=".Length)..].Trim();
        }

        s = s.Trim();
        if (s.StartsWith('"') && s.EndsWith('"'))
        {
            s = s[1..^1];
        }

        return s.Trim();
    }

    public static async Task<(bool Ok, string? UserMessage, string? AuthTicket, bool ChallengeRequired)> GetAuthenticationTicketAsync(
        HttpClient client,
        Func<string> getCookie,
        Action<string> setCookie,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket");
            req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={getCookie()}");
            req.Headers.Add("Origin", "https://www.roblox.com");
            req.Headers.Add("Referer", "https://www.roblox.com/");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var r = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            RobloxLaunchServiceApplySetCookie(r, setCookie);

            if (r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                && r.Headers.TryGetValues("x-csrf-token", out var toks))
            {
                var t = toks.FirstOrDefault();
                var peek = await r.Content.ReadAsStringAsync(ct);
                if (LooksLikeChallengeBody(peek) || HasChallengeHeader(r))
                {
                    return (false, "Roblox asked for bot verification.", null, true);
                }

                if (!string.IsNullOrEmpty(t))
                {
                    if (client.DefaultRequestHeaders.Contains("X-CSRF-TOKEN"))
                    {
                        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
                    }

                    client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", t);
                }

                continue;
            }

            var body = await r.Content.ReadAsStringAsync(ct);
            if (LooksLikeChallengeBody(body) || HasChallengeHeader(r))
            {
                return (false, "Roblox asked for bot verification.", null, true);
            }

            if (!r.IsSuccessStatusCode)
            {
                return (false, $"Authentication service returned {(int)r.StatusCode}. Copy a fresh .ROBLOSECURITY from a browser where you are logged in.", null, false);
            }

            foreach (var h in r.Headers)
            {
                if (h.Key.Contains("ticket", StringComparison.OrdinalIgnoreCase)
                    && h.Value.FirstOrDefault() is { } hv
                    && !string.IsNullOrEmpty(hv))
                {
                    return (true, null, hv, false);
                }
            }

            if (TryGetTicketFromJson(body, out var fromJson) && fromJson is not null)
            {
                return (true, null, fromJson, false);
            }

            return (false, "Roblox returned OK but no authentication ticket. Get a new .ROBLOSECURITY and try again.", null, false);
        }

        return (false, "Could not get a security token (CSRF). Try a new .ROBLOSECURITY from a browser where you are logged in.", null, false);
    }

    // Shared with RobloxLaunchService — keep Set-Cookie handling in one place via duplicate small helper to avoid circular visibility issues.
    private static void RobloxLaunchServiceApplySetCookie(HttpResponseMessage response, Action<string> setCookie)
    {
        IEnumerable<string>? values = null;
        if (response.Headers.TryGetValues("Set-Cookie", out var headerValues))
        {
            values = headerValues;
        }
        else if (response.Content?.Headers.TryGetValues("Set-Cookie", out var contentValues) == true)
        {
            values = contentValues;
        }

        if (values is null)
        {
            return;
        }

        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            const string prefix = ".ROBLOSECURITY=";
            var idx = raw.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var start = idx + prefix.Length;
            var end = raw.IndexOf(';', start);
            var value = end > start ? raw[start..end] : raw[start..];
            value = Sanitize(Uri.UnescapeDataString(value.Trim()));
            if (!string.IsNullOrEmpty(value))
            {
                setCookie(value);
            }
        }
    }

    private static bool HasChallengeHeader(HttpResponseMessage response)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.Contains("challenge", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeChallengeBody(string? body) =>
        !string.IsNullOrEmpty(body)
        && (body.Contains("Challenge is required", StringComparison.OrdinalIgnoreCase)
            || body.Contains("\"challengeType\"", StringComparison.OrdinalIgnoreCase)
            || body.Contains("rblx-challenge", StringComparison.OrdinalIgnoreCase));

    private static bool TryGetTicketFromJson(string? body, out string? ticket)
    {
        ticket = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var d = JsonDocument.Parse(body);
            foreach (var name in new[] { "ticket", "authenticationTicket", "authentication" })
            {
                if (d.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        ticket = s;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }
}

public readonly record struct LaunchResult(bool Ok, string Message, string? UpdatedCookie = null, long? BrowserTrackerId = null);
