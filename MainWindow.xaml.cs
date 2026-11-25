using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MonitorInputWizzard;

public partial class MainWindow : Window
{
    private readonly MonitorController _monitors = new();
    private readonly Settings _settings = Settings.Load();
    private readonly ObservableCollection<InputPreset> _inputs;
    private HotkeyManager? _hotkeys;
    private NvApi? _nvApi;
    private InputPreset? _capturing;
    private readonly Forms.NotifyIcon _tray;
    private bool _exiting;
    private readonly DispatcherTimer _trimTimer;
    private bool _lastKeyExtended;   // from the raw WM_KEYDOWN: dedicated nav keys are "extended", numpad ones aren't

    public MainWindow()
    {
        InitializeComponent();
        _inputs = new(_settings.Inputs);
        InputList.ItemsSource = _inputs;
        InputRegBox.Text = "0x" + _settings.InputRegister.ToString("X2");
        SrcBox.Text = "0x" + _settings.SourceAddr.ToString("X2");
        NvCheck.IsChecked = _settings.UseNvApi;
        MonitorRow.IsEnabled = !_settings.UseNvApi;
        AutostartCheck.IsChecked = Autostart.IsEnabled;
        PreviewKeyDown += OnCaptureKey;
        _tray = CreateTray();
        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); if (!IsVisible) TrimMemory(); };
        // SourceInitialized (not Loaded) so hotkeys work when the window is started hidden in the tray.
        SourceInitialized += (_, _) => { HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(KeyHook); _hotkeys = new(this); RefreshMonitors(); ReportHotkeys(RegisterHotkeys()); };
        Deactivated += (_, _) => EndCapture();
        // Close/minimize hide to the tray so hotkeys keep working; the tray menu's Exit really quits.
        Closing += (_, e) => { if (!_exiting) { e.Cancel = true; Hide(); _trimTimer.Start(); } };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { Hide(); _trimTimer.Start(); } };
        Application.Current.SessionEnding += (_, _) => _exiting = true;
        Closed += (_, _) => { _tray.Dispose(); _hotkeys?.Dispose(); _nvApi?.Dispose(); _monitors.Dispose(); };
    }

    private IntPtr KeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is 0x0100 or 0x0104) _lastKeyExtended = (lParam.ToInt64() & (1L << 24)) != 0;
        return IntPtr.Zero;
    }

    // ---- Tray ----

    private Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var p in _inputs)
            {
                var item = new Forms.ToolStripMenuItem(p.Name) { ShortcutKeyDisplayString = p.HasHotkey ? p.HotkeyText : null };
                item.Click += (_, _) => SwitchTo(p);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Show window", null, (_, _) => ShowFromTray());
            menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        };
        var tray = new Forms.NotifyIcon { Text = "Monitor Input Wizzard", Icon = TrayIcon(), ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
        return tray;
    }

    private static System.Drawing.Icon TrayIcon()
    {
        try
        {
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("app.ico");
            if (s is not null) return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
        }
        catch { /* fall back to the stock icon */ }
        return System.Drawing.SystemIcons.Application;
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>While idle in the tray, collect garbage and release resident pages; they fault back in on demand.</summary>
    private static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }

    /// <summary>Creates the window handle without showing it, so hotkeys register while only the tray icon is visible.</summary>
    public void StartInTray()
    {
        new WindowInteropHelper(this).EnsureHandle();
        _trimTimer.Start();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private static byte ParseHexByte(string s, byte fallback)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return byte.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var b) ? b : fallback;
    }

    private void RefreshMonitors()
    {
        var list = _monitors.Enumerate();
        MonitorBox.ItemsSource = list;
        MonitorBox.SelectedItem = list.FirstOrDefault(m => m.Description == _settings.MonitorDescription);
        if (MonitorBox.SelectedItem is null && list.Count > 0) MonitorBox.SelectedIndex = 0;
        Status.Text = list.Count == 0 ? "No DDC/CI monitors found." : $"{list.Count} monitor(s).";
    }

    private MonitorController.Monitor? Selected =>
        MonitorBox.SelectedItem is MonitorController.Monitor m ? m : null;

    private byte InputRegister => ParseHexByte(InputRegBox.Text, 0x60);

    /// <summary>Re-registers all hotkeys. Returns a warning naming every hotkey the OS rejected, or null.</summary>
    private string? RegisterHotkeys()
    {
        if (_hotkeys is null) return null;
        _hotkeys.Dispose();
        _hotkeys = new(this);
        var failed = new List<string>();
        foreach (var input in _inputs.Where(i => i.HasHotkey))
        {
            var target = input;
            if (_hotkeys.Register(input.Modifiers, input.Key, () => SwitchTo(target)) is null)
                failed.Add($"\"{input.Name}\" ({input.HotkeyText})");
            // With NumLock off the numpad sends nav keys (NumPad1 -> End), so also bind that alias.
            else if (NumLockOffAlias(input.Key) is { } alias)
                _hotkeys.Register(input.Modifiers, alias, () => SwitchTo(target));
        }
        return failed.Count == 0 ? null : "Hotkey already in use: " + string.Join(", ", failed) + ".";
    }

    private static Key? NumLockOffAlias(Key k) => k switch
    {
        Key.NumPad0 => Key.Insert, Key.NumPad1 => Key.End, Key.NumPad2 => Key.Down, Key.NumPad3 => Key.Next,
        Key.NumPad4 => Key.Left, Key.NumPad5 => Key.Clear, Key.NumPad6 => Key.Right, Key.NumPad7 => Key.Home,
        Key.NumPad8 => Key.Up, Key.NumPad9 => Key.Prior, Key.Decimal => Key.Delete, _ => null,
    };

    private void ReportHotkeys(string? warning) { if (warning is not null) Status.Text = warning; }

    private void SwitchTo(InputPreset input)
    {
        SwitchCore(input);
        // No window to read the status from while hidden in the tray, so surface it as a balloon.
        if (!IsVisible) { _tray.ShowBalloonTip(2000, input.Name, Status.Text, Forms.ToolTipIcon.None); _trimTimer.Start(); }
    }

    private void SwitchCore(InputPreset input)
    {
        byte reg = InputRegister;
        if (NvCheck.IsChecked == true)
        {
            try { _nvApi ??= new NvApi(); }
            catch (Exception ex) { Status.Text = "NVAPI unavailable: " + ex.Message; return; }
            string log = _nvApi.SetVcp(reg, (ushort)input.Code, ParseHexByte(SrcBox.Text, 0x50));
            Status.Text = $"NVAPI {input.Name} VCP 0x{reg:X2}=0x{input.Code:X2} src 0x{ParseHexByte(SrcBox.Text, 0x50):X2}: {log.Trim()}";
            return;
        }
        if (Selected is not { } m) { Status.Text = "No monitor selected."; return; }
        bool ok = _monitors.SetVcp(m.Handle, reg, input.Code);
        Status.Text = ok
            ? $"Sent {input.Name} (VCP 0x{reg:X2} = 0x{input.Code:X2}). If nothing changed, wrong register/code for this monitor."
            : $"SetVCPFeature failed for {input.Name} (VCP 0x{reg:X2} = 0x{input.Code:X2}).";
    }

    // ---- Hotkey capture ----

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        _capturing = ((FrameworkElement)sender).DataContext as InputPreset;
        // Unregister ours while capturing: RegisterHotKey swallows an already-bound combo before WPF sees it.
        _hotkeys?.Dispose();
        _hotkeys = new(this);
        Status.Text = "Hold your modifiers (e.g. Ctrl+Alt+Shift), then press a key. Esc clears.";
    }

    private void EndCapture()
    {
        if (_capturing is null) return;
        _capturing = null;
        Status.Text = "Capture cancelled.";
        ReportHotkeys(RegisterHotkeys());
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string Describe(ModifierKeys mods) => InputPreset.FormatModifiers(mods);

    private static int CountModifiers(ModifierKeys m) =>
        ((m & ModifierKeys.Control) != 0 ? 1 : 0) + ((m & ModifierKeys.Alt) != 0 ? 1 : 0) +
        ((m & ModifierKeys.Shift) != 0 ? 1 : 0) + ((m & ModifierKeys.Windows) != 0 ? 1 : 0);

    private void OnCaptureKey(object sender, KeyEventArgs e)
    {
        if (_capturing is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (IsModifierKey(key))
        {
            // Swallow modifier presses so Alt/Win don't trigger menu or shell behaviour mid-capture.
            e.Handled = true;
            Status.Text = $"Capturing: {Describe(mods)}...";
            return;
        }

        e.Handled = true;

        if (key is Key.None or Key.DeadCharProcessed or Key.ImeProcessed)
        {
            Status.Text = $"Couldn't read that key (WPF reported Key={e.Key}, SystemKey={e.SystemKey}). Try a different key.";
            return;
        }

        // Shift+NumPad with NumLock on: Windows sends a fake Shift-up plus the nav key (NumPad1 -> End), so WPF
        // sees e.g. plain Ctrl+End and RegisterHotKey could never match the real combo. Refuse it.
        if (!_lastKeyExtended && Keyboard.IsKeyToggled(Key.NumLock) &&
            key is Key.End or Key.Down or Key.Next or Key.Left or Key.Clear or Key.Right or Key.Home or Key.Up or Key.Prior or Key.Insert or Key.Delete)
        {
            Status.Text = $"Shift+NumPad with NumLock on arrives as {key} and can't be used. Use Ctrl+Alt+NumPad (no Shift) or an F-key.";
            return;
        }

        if (key == Key.Escape)
        {
            _capturing.Modifiers = ModifierKeys.None;
            _capturing.Key = Key.None;
            Status.Text = "Hotkey cleared.";
        }
        else
        {
            _capturing.Modifiers = mods;
            _capturing.Key = key;
            int count = CountModifiers(mods);
            string combo = $"{Describe(mods)}{key}";
            Status.Text = count >= 2
                ? $"Captured {combo} (WPF saw Key={e.Key}, SystemKey={e.SystemKey})."
                : $"Captured {combo}, only {count} modifier(s): may clash with app shortcuts. Try Ctrl+Alt+Shift+key.";
        }
        _capturing = null;
        if (RegisterHotkeys() is { } warning) Status.Text += " " + warning;
    }

    // ---- Buttons ----

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InputPreset p) SwitchTo(p);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
        => _inputs.Add(new() { Name = "New input", Code = 0x0F });

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InputPreset p)
        {
            _inputs.Remove(p);
            ReportHotkeys(RegisterHotkeys());
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Inputs = _inputs.ToList();
        _settings.MonitorDescription = Selected?.Description;
        _settings.InputRegister = InputRegister;
        _settings.UseNvApi = NvCheck.IsChecked == true;
        _settings.SourceAddr = ParseHexByte(SrcBox.Text, 0x50);
        _settings.Save();
        Status.Text = "Saved. " + RegisterHotkeys();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        bool on = AutostartCheck.IsChecked == true;
        try
        {
            Autostart.Set(on);
            Status.Text = on ? "Will start with Windows, minimized to the tray." : "Start with Windows turned off.";
        }
        catch (Exception ex)
        {
            AutostartCheck.IsChecked = !on;
            Status.Text = "Couldn't update the startup entry: " + ex.Message;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshMonitors();

    // NVAPI broadcasts to all displays, so the dxva2 monitor picker/Read/Test don't apply in that mode.
    private void NvMode_Changed(object sender, RoutedEventArgs e) => MonitorRow.IsEnabled = NvCheck.IsChecked != true;

    // Dims brightness for ~1s then restores it, to prove DDC/CI writes actually reach the panel.
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } m) { Status.Text = "No monitor selected."; return; }
        if (_monitors.GetVcp(m.Handle, 0x10) is not { } b) { Status.Text = "Can't read brightness (0x10) — DDC/CI read failed."; return; }
        bool wrote = _monitors.SetVcp(m.Handle, 0x10, Math.Min(20, b.max / 4));
        Status.Text = wrote ? "Dimming... watch the screen." : "Brightness write returned failure.";
        await Task.Delay(1200);
        _monitors.SetVcp(m.Handle, 0x10, b.cur);
        Status.Text = wrote ? "Restored. If it dimmed, DDC writes work — the input issue is the code or auto-switch." : Status.Text;
    }

    private void Read_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } m) { Status.Text = "No monitor selected."; return; }
        byte reg = InputRegister;
        var code = _monitors.GetVcp(m.Handle, reg)?.cur;
        Status.Text = code is { } c
            ? $"VCP 0x{reg:X2} current = 0x{c:X2}  (set an input's code to this)"
            : $"Could not read VCP 0x{reg:X2} — unsupported, or DDC/CI disabled in the OSD.";
    }

    private void Monitor_Changed(object sender, SelectionChangedEventArgs e)
        => _settings.MonitorDescription = Selected?.Description;
}
