using System.Runtime.InteropServices;
using System.Text;

namespace RobloxUtility.Services;

/// <summary>Writes to an attached console (e.g. when started with <c>dotnet run</c>) with a clear banner.</summary>
public static class AppLog
{
    /// <summary>Raised for every log line (mirrors console text). Handlers may be off the UI thread.</summary>
    public static event Action<string>? UiLogLine;

    private static readonly object Gate = new();
    private static bool _initialized;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    public static void Init()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                // Do not create/attach a console for GUI usage.
                // If the process already has a console (e.g. started from a terminal), we log to it.
                if (GetConsoleWindow() == IntPtr.Zero)
                {
                    return;
                }

                Console.OutputEncoding = Encoding.UTF8;
                Console.Title = "Roblox Utility — activity log";
                WriteBanner();
                _initialized = true;
            }
            catch
            {
                // no console available
            }
        }
    }

    private static void WriteBanner()
    {
        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine("  ═══════════════════════════════════════════════════════════════");
            Console.WriteLine("    Roblox Utility — activity log");
            Console.WriteLine("    Multi-instance · Accounts · Places · Auto Clicker");
            Console.WriteLine("  ═══════════════════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine("  Tip: launches, saves, and errors are echoed here when a console is");
            Console.WriteLine("       attached (e.g. run:  dotnet run --project RobloxUtility\\RobloxUtility.csproj )");
            Console.WriteLine();
        }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }

    public static void Line(string category, string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        var ui = $"[{ts}] {category,-10} {message}";
        UiLogLine?.Invoke(ui);

        lock (Gate)
        {
            if (!_initialized)
            {
                return;
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{ts}] ");
            Console.ForegroundColor = CategoryColor(category);
            Console.Write($"{category,-10} ");
            Console.ResetColor();
            Console.WriteLine(message);
        }
    }

    public static void Info(string message) => Line("INFO", message);

    public static void Ok(string message) => Line("OK", message);

    public static void Warn(string message) => Line("WARN", message);

    public static void Err(string message) => Line("ERROR", message);

    private static ConsoleColor CategoryColor(string c) => c.ToUpperInvariant() switch
    {
        "OK" => ConsoleColor.Green,
        "WARN" => ConsoleColor.Yellow,
        "ERROR" => ConsoleColor.Red,
        "LAUNCH" => ConsoleColor.Magenta,
        "SAVE" => ConsoleColor.Cyan,
        "MULTI" => ConsoleColor.DarkCyan,
        _ => ConsoleColor.Gray
    };
}
