namespace RobloxUtility.Models;

/// <summary>Roblox userPresenceType from presence.roblox.com, plus local states for UI.</summary>
public enum AccountPresenceKind
{
    Unknown = -2,
    NoCookie = -1,
    InvalidCookie = -3,
    Offline = 0,
    Online = 1,
    InGame = 2,
    InStudio = 3
}
