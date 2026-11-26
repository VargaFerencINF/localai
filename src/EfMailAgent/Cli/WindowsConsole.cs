using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EfMailAgent.Cli;

/// <summary>
/// Desktop-app behaviour on Windows: when the exe was started with a double click (the console window was created
/// only for it), the console is released so no black window stays open; errors are shown in a message box.
/// Started from a command prompt the console stays (the prompt also owns it).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsConsole
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint hWnd, string text, string caption, uint type);

    /// <summary>Releases the console if this process is the only one attached to it (double click). True if released.</summary>
    public static bool DetachIfOwnConsole()
    {
        var list = new uint[4];
        var count = GetConsoleProcessList(list, (uint)list.Length);
        if (count != 1)
            return false;
        if (!FreeConsole())
            return false;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        return true;
    }

    public static void ShowError(string text) => MessageBox(0, text, "EfMailAgent", 0x10 /* MB_ICONERROR */);
}
