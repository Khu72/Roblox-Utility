# Roblox Utility

Windows desktop app (WPF, .NET 8) for multi-instance helpers, saved accounts/places, launching with a cookie, an auto clicker, and an in-app log.

## For people who only want to run the app (no SDK, no .NET install)

You **cannot** build this project without the .NET SDK — but you **do not need** the SDK or the .NET runtime on your PC if you use a **self-contained** build.

1. **GitHub (recommended after you enable Actions)**  
   - Open the **Actions** tab → workflow **Build portable (self-contained)** → run it, or push a tag like `v1.0.0` to trigger it and attach the ZIP to a Release.  
   - Download **`RobloxUtility-win-x64-self-contained.zip`** from the run’s **Artifacts** (or from **Releases** if you use version tags).  
   - Extract the ZIP and run **`RobloxUtility.exe`** (self-contained single file; you can move just the EXE if you prefer).

2. **Someone built it for you**  
   - They run **`Publish-FriendExe.cmd`** (or the MSBuild target below) and share **`RobloxUtility.exe`** from the `publish` folder.

There is no supported way to make **cloning the repo and compiling** work without installing the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — that is what compiles C#.

---

## For developers (build from source)

**Requirements:** Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

From the repository root (where `RobloxUtility.sln` lives):

```powershell
dotnet restore
dotnet build .\RobloxUtility\RobloxUtility.csproj -c Release
dotnet run --project .\RobloxUtility\RobloxUtility.csproj -c Release
```

### One-command portable build (maintainers)

From the repo root (next to `RobloxUtility.sln`):

```powershell
.\Publish-FriendExe.cmd
```

Or:

```powershell
dotnet msbuild .\RobloxUtility\RobloxUtility.csproj -t:PublishFriendExe
```

Output: `RobloxUtility\bin\Release\net8.0-windows\win-x64\publish\RobloxUtility.exe`

### Manual `dotnet publish` (same settings as CI)

`EnableCompressionInSingleFile` must stay **false** or the EXE may fail to start.

```powershell
dotnet publish .\RobloxUtility\RobloxUtility.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=false `
  -p:PublishTrimmed=false `
  -o "D:\Path\You\Choose\RobloxUtility"
```

**Smaller download** for people who **already have** [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0):

```powershell
dotnet publish .\RobloxUtility\RobloxUtility.csproj -c Release -r win-x64 `
  -p:PublishSingleFile=true -p:SelfContained=false `
  -o "D:\Path\You\Choose\RobloxUtility"
```

## Application icon

- **`RobloxUtility\Assets\brand.png`** — in-app logo and the source artwork (window title bar uses this via WPF resources).
- **`RobloxUtility\Assets\app.ico`** — EXE / File Explorer icon (referenced by `<ApplicationIcon>` in the `.csproj`). Regenerate it from `brand.png` if you swap the artwork (any tool that produces a valid `.ico`, or embed the PNG in an ICO container).

If `app.ico` is missing, the build fails. To ship without a custom EXE icon, remove the `<ApplicationIcon>` line from `RobloxUtility.csproj`.

## Where data is stored

Settings and saved lists are written under your user profile, for example:

`%AppData%\RobloxUtility\`

(accounts, places, auto-clicker preferences, etc.). Do not commit those files; they are created at runtime.

## Security

Saved **`.ROBLOSECURITY`** cookies are sensitive (like a password). This app stores them locally with Windows data protection. Never share your cookie or upload real `accounts.json` / screenshots containing it.

## Multi-instance note

The multi-instance action may need **Run as administrator** on some systems, depending on Roblox version and Windows security settings.

## Terms and disclaimers

**Use at your own risk.** The authors are **not responsible** for Roblox account **warnings, bans, or any moderation** that may result from using this software. See **[TERMS.md](./TERMS.md)** for the full text, including how this project is described in relation to Roblox’s rules and why **no one can promise** your use will always comply with Roblox’s current Terms of Use (only Roblox decides enforcement).

## License

Copyright © 2026 **Khu72**. All rights reserved. See **[LICENSE.md](./LICENSE.md)** for ownership, permitted use, and restrictions on distribution.

**Before you use or share this software, read [TERMS.md](./TERMS.md)** (disclaimers, Roblox-related notices, and your responsibilities).