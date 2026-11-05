using System.Runtime.InteropServices;

namespace Rdap.Cli;

internal static partial class Ansi
{
    public const string Reset = "\e[0m";
    public const string Bold = "\e[1m";
    public const string Dim = "\e[2m";
    public const string Cyan = "\e[36m";
    public const string Yellow = "\e[33m";
    public const string Red = "\e[31m";

    /// <summary>Colour only for an interactive terminal, honouring https://no-color.org.</summary>
    public static bool ShouldUseColor()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) ||
            Console.IsOutputRedirected ||
            string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal))
        {
            return false;
        }

        return !OperatingSystem.IsWindows() || TryEnableWindowsVirtualTerminal();
    }

    // Windows Terminal handles ANSI natively; the legacy console host needs VT processing switched on.
    private static bool TryEnableWindowsVirtualTerminal()
    {
        const int StdOutputHandle = -11;
        const uint EnableVirtualTerminalProcessing = 0x0004;

        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetConsoleMode(handle, out var mode))
        {
            return false;
        }

        return (mode & EnableVirtualTerminalProcessing) != 0 ||
               SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
