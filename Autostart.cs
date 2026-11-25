using Microsoft.Win32;

namespace MonitorInputWizzard;

/// <summary>Per-user "start with Windows" via HKCU Run key (no admin). Launches the exe with --tray.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorInputWizzard";

    public static bool IsEnabled
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(ValueName) is string; }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
