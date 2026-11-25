using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace MonitorInputWizzard
{
    public partial class App : Application
    {
        private const string TrayArg = "--tray";
        private static Mutex? _singleInstance;   // held for the process lifetime

        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Headless mode: `MonitorInputWizzard.exe <input name>` switches and exits, no window.
            // Exit codes: 0 = sent, 1 = switch failed (NVAPI/DDC error), 2 = unknown input name.
            string? target = ParseTarget(e.Args);
            if (target is not null)
            {
                Shutdown(HeadlessSwitch(target));
                return;
            }

            // GUI mode is single-instance: a second launch just surfaces the running one.
            _singleInstance = new Mutex(true, @"Local\MonitorInputWizzard", out bool first);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MonitorInputWizzard.Show");
            if (!first) { showEvent.Set(); Shutdown(); return; }
            new Thread(() =>
            {
                while (showEvent.WaitOne())
                    Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
            }) { IsBackground = true }.Start();

            DispatcherUnhandledException += (_, args) =>
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "miw-crash.txt"), args.Exception.ToString());
                MessageBox.Show(args.Exception.ToString(), "Crash");
                args.Handled = true;
            };

            var window = new MainWindow();
            if (e.Args.Contains(TrayArg)) window.StartInTray(); else window.Show();
        }

        // Accepts "HDMI1", "HDMI 1", or "--switch HDMI1". Returns null if no switch requested.
        private static string? ParseTarget(string[] args)
        {
            if (args.Length == 0 || args[0] == TrayArg) return null;
            var parts = args[0] is "--switch" or "-s" ? args.Skip(1) : args;
            var name = string.Join(" ", parts).Trim();
            return name.Length == 0 ? null : name;
        }

        private static string Norm(string s) => s.Replace(" ", "").ToLowerInvariant();

        private static int HeadlessSwitch(string name)
        {
            try
            {
                var s = Settings.Load();
                var preset = s.Inputs.FirstOrDefault(i => Norm(i.Name) == Norm(name));
                if (preset is null)
                {
                    Fail($"Unknown input \"{name}\". Known: {string.Join(", ", s.Inputs.Select(i => i.Name))}.");
                    return 2;
                }

                if (s.UseNvApi)
                {
                    using var nv = new NvApi();
                    string log = nv.SetVcp(s.InputRegister, (ushort)preset.Code, s.SourceAddr);
                    if (!log.Contains(":OK]")) { Fail($"NVAPI switch to \"{preset.Name}\" failed: {log.Trim()}"); return 1; }
                }
                else
                {
                    using var mc = new MonitorController();
                    var mon = mc.Enumerate().FirstOrDefault(m => m.Description == s.MonitorDescription);
                    if (mon.Handle == IntPtr.Zero) { Fail("Saved monitor not found. Open the app and pick one, then Save."); return 1; }
                    if (!mc.SetVcp(mon.Handle, s.InputRegister, preset.Code)) { Fail($"SetVCPFeature failed for \"{preset.Name}\"."); return 1; }
                }
                return 0;
            }
            catch (Exception ex)
            {
                // e.g. nvapi64.dll missing (no NVIDIA driver) or a null NVAPI function pointer.
                Fail($"Switch failed: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        // Console message when launched from a terminal; dialog when launched from a shortcut/hotkey (no console to read).
        private static void Fail(string message)
        {
            if (AttachConsole(-1))
            {
                try { using var err = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true }; err.WriteLine("MonitorInputWizzard: " + message); return; }
                catch { /* fall through to dialog */ }
            }
            MessageBox.Show(message, "Monitor Input Wizzard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
