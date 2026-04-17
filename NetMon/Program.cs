using System.Runtime.InteropServices;
using System.Threading;

namespace NetMon;

static class Program
{
    [STAThread]
    static void Main()
    {
        // ── Single-instance guard ────────────────────────────────────────
        // If NetMon is already running, ask the running instance to show
        // itself instead of launching a second copy.
        using var mutex = new Mutex(false,
            "NetMon_Singleton_{8F4E2A1B-C3D5-4E6F-A7B8-9C0D1E2F3A4B}",
            out bool createdNew);

        if (!createdNew)
        {
            // Broadcast the "show me" message — the running instance picks
            // it up in WndProc and un-hides its window.
            PostMessage(HWND_BROADCAST, MainForm.WmShowNetMon, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────

    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
