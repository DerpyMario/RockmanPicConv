using System.Runtime.InteropServices;

namespace PacTool.Cli;

/// <summary>
/// Tells a run started by dropping files on the executable apart from one typed at a prompt.
///
/// It matters because of what happens at the end: a console the shell already owned stays open and
/// keeps the output on screen, while one created for this process is destroyed the moment the
/// process exits, taking every line with it. Someone who drags an archive onto the tool would see a
/// window flash and nothing else. So when this process is the only one attached to its console, the
/// run pauses before returning.
/// </summary>
public static class ConsoleSession
{
    /// <summary>
    /// True when this process created the console it is writing to, which is what happens when it
    /// is launched from a file manager rather than from a shell. Always false where the question
    /// does not arise: redirected output, and platforms whose file managers do not do this.
    /// </summary>
    public static bool OwnsConsole()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
            return false;
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var processes = new uint[4];
            return GetConsoleProcessList(processes, (uint)processes.Length) == 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Waits for a key so the window stays up long enough to read. Silent if it cannot.</summary>
    public static void Pause(string message = "Press any key to close this window . . .")
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine(message);
            Console.ReadKey(intercept: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // No console to read from after all; there is nothing to wait for.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint count);
}
