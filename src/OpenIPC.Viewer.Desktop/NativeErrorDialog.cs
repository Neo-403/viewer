using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenIPC.Viewer.Core.Expiration;

namespace OpenIPC.Viewer.Desktop;

/// <summary>
/// OS-level error dialog for blocked time-limited builds. Nothing here mentions
/// expiry or build verification: a time failure looks like a network problem,
/// an expired build looks like a system compatibility problem.
/// </summary>
internal static class NativeErrorDialog
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;
    private const uint MbTopmost = 0x00040000;

    public static void Show(ExpirationBlockReason reason)
    {
        var (title, message) = TextFor(reason);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            MessageBoxW(IntPtr.Zero, message, title,
                MbOk | MbIconError | MbSetForeground | MbTopmost);
            return;
        }

        Console.Error.WriteLine($"{title}: {message}");
    }

    private static (string Title, string Message) TextFor(ExpirationBlockReason reason)
    {
        var chinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (reason == ExpirationBlockReason.TimeUnavailable)
        {
            return chinese
                ? ("网络连接", "无网络环境，需联网后使用")
                : ("Network Connection", "No network available. Connect to the network before using this application.");
        }

        return chinese
            ? ("系统错误", "当前系统环境不支持此应用程序，无法继续运行。")
            : ("System Error", "The current system environment does not support this application.");
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
}
