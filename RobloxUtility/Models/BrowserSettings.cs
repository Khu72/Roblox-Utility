namespace RobloxUtility.Models;

public sealed class BrowserChoice
{
    public required string Value { get; init; }

    public required string Label { get; init; }
}

public sealed class BrowserSettings
{
    public const string DefaultBrowser = "Default";
    public const string Chrome = "Chrome";
    public const string Edge = "Edge";
    public const string Firefox = "Firefox";
    public const string Brave = "Brave";
    public const string Opera = "Opera";

    public static IReadOnlyList<BrowserChoice> BrowserChoices { get; } =
    [
        new() { Value = DefaultBrowser, Label = "Default (system)" },
        new() { Value = Chrome, Label = "Google Chrome" },
        new() { Value = Edge, Label = "Microsoft Edge" },
        new() { Value = Firefox, Label = "Mozilla Firefox" },
        new() { Value = Brave, Label = "Brave" },
        new() { Value = Opera, Label = "Opera" }
    ];

    public static IReadOnlyList<string> AllBrowsers { get; } =
        BrowserChoices.Select(c => c.Value).ToArray();

    public string Browser { get; set; } = DefaultBrowser;

    public bool OpenInPrivate { get; set; }
}
