# MonitorInputWizzard

A small Windows WPF app that switches a monitor's input source (HDMI 1, DisplayPort 2, USB-C and so on) with a global hotkey or from the command line, so you don't have to use the monitor's on-screen menu.

It sends the switch over DDC/CI, the control channel that runs alongside the video cable. There are two ways to send it:

| Transport | How it works | Use it when |
|---|---|---|
| dxva2 | Windows `SetVCPFeature` writing VCP register `0x60` | The monitor follows the standard |
| NVAPI raw I2C | Writes the DDC/CI packet straight to the I2C bus of an NVIDIA GPU | The monitor ignores standard writes (LG UltraGear, see below) |

## Why there is an NVAPI path

Most monitors switch input when they get a standard `SetVCPFeature(0x60, <input code>)`. Some don't. LG UltraGear monitors (confirmed on the 27GN800) ignore register `0x60`. Their input select is a non-standard register, `0xF4`, and it is only reachable through the `0x50` service address, which the Windows dxva2 API never uses.

`NvApi.cs` builds that DDC/CI packet by hand (length, Set VCP opcode, register, value, checksum) and sends it with `NvAPI_I2CWrite`. The defaults in the app are set up for LG UltraGear. On a normal monitor, untick **NVIDIA I2C** to use the standard path.

## Requirements

- Windows 10 or 11, x64
- .NET 10 SDK to build. The published exe is self-contained, so nothing needs installing to run it.
- DDC/CI turned on in the monitor's on-screen menu. Many monitors ship with it off.
- An NVIDIA GPU and driver, only if you use the NVAPI transport (`nvapi64.dll` comes with the driver)

## Build and run

```
git clone https://github.com/Lunat1q/MonitorInputWizzard.git
cd MonitorInputWizzard
dotnet run
```

Single-file release build:

```
dotnet publish -c Release
```

The exe ends up in `bin/Release/net10.0-windows/win-x64/publish/`. It is around 150 MB because single-file compression is turned off. Compression keeps every decompressed assembly in private memory, and turning it off cuts idle memory a lot.

## Using it

The window is a list of input presets. Each preset has a name, a VCP code and an optional global hotkey.

1. Choose the transport. Keep **NVIDIA I2C** ticked for an LG UltraGear. Untick it for a standard monitor, which enables the monitor picker.
2. Set **Input VCP**. `0x60` is the standard register and `0xF4` is LG UltraGear.
3. Find your input codes. Switch the monitor to an input by hand, then press **Read current**. It shows the register's current value, which is that input's code. Repeat for each input and type the values into the code column.
4. Bind a hotkey. Click a preset's hotkey button, then press the combination. Esc clears it.
5. Press **Save**. Settings are stored in `%AppData%\MonitorInputWizzard\settings.json`.

**Test DDC** dims the selected monitor for about a second and then restores it. If the screen dims, DDC/CI writes are reaching the monitor, so a failed input switch means a wrong register or code. It only works in dxva2 mode.

### Hotkeys

Hotkeys are system-wide (`RegisterHotKey`, auto-repeat suppressed) and work while the window is hidden.

- Use at least two modifiers (for example Ctrl+Alt+F5) so they don't collide with other programs. The app warns if you use fewer.
- Shift+NumPad doesn't work when NumLock is on. Windows sends End, Home and so on instead, and the app refuses to capture it.
- A hotkey on a numpad key also works with NumLock off. The app registers the nav key that the numpad sends in that state, so Ctrl+Alt+NumPad1 also answers to Ctrl+Alt+End.
- Some other programs already use combinations like Ctrl+Shift+number (screenshot tools, for example). If a hotkey does nothing, try a different one.
- While you capture a hotkey, the app's own hotkeys are suspended, so you can re-capture a combination that is already bound.

### Default presets

For LG UltraGear: register `0xF4`, NVIDIA I2C on, source address `0x50`.

| Input | Code |
|---|---|
| DisplayPort 1 | `0xD0` |
| DisplayPort 2 | `0xD1` |
| HDMI 1 | `0x90` |
| HDMI 2 | `0x91` |
| USB-C | `0xD2` |

For a standard monitor on register `0x60`, the usual codes are `0x0F` (DisplayPort 1), `0x10` (DisplayPort 2), `0x11` (HDMI 1) and `0x12` (HDMI 2).

## Running in the tray

Closing or minimizing the window hides it to a tray icon, so hotkeys keep working. Left-click the icon to show the window. The right-click menu lists your presets, plus **Show window** and **Exit**. Exit is the only way to quit.

- Only one copy runs. Starting a second one shows the window of the first.
- `MonitorInputWizzard.exe --tray` starts with the window hidden.
- **Start with Windows** adds a per-user entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` that launches the exe with `--tray`. It is off by default and needs no admin rights. The entry stores the path of the exe you ticked it from, so tick it from the copy you plan to keep. Untick it to remove the entry.
- When the window is hidden, a switch shows its result as a tray notification.
- While hidden, the app trims its memory use to a few MB.

## Command line

Passing an input name switches to it and exits, with no window:

```
MonitorInputWizzard.exe "HDMI 1"
MonitorInputWizzard.exe HDMI1          # spaces and case are ignored
MonitorInputWizzard.exe --switch "USB-C"
```

It uses the saved settings, so set things up once in the window. You can then call the exe from a Stream Deck button, an AutoHotkey script or a shortcut.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | The switch was sent |
| 1 | The switch failed (NVAPI or DDC error, driver missing, monitor not found) |
| 2 | No preset with that name |

On failure it prints a message to stderr when started from a terminal. When there is no console, for example from a shortcut, it shows a dialog instead.

## Troubleshooting

**Nothing happens.** Check that DDC/CI is on in the monitor menu. Then press **Test DDC**. If the brightness doesn't change, DDC/CI isn't getting through at all. Likely causes are a bad cable or adapter, DDC/CI turned off, or a KVM or dock in the path.

**Test DDC dims the screen but the input doesn't switch.** The register or code is wrong. Use **Read current** on each input to find the real codes. If `0x60` reads back as unsupported, try `0xF4` with NVIDIA I2C ticked.

**"No DDC/CI monitors found."** Windows sees no monitor with a physical DDC/CI handle. This is common with DisplayLink and other USB display adapters, and with some laptop panels. They can't be controlled this way.

**"Hotkey already in use."** Another program registered that combination first. The message lists every hotkey that was rejected. Pick a different combination.

**Switching to an input works from the PC but not back.** Some monitors may ignore DDC from a source they aren't currently showing. This is untested here and depends on the monitor.

## Layout

| File | What it does |
|---|---|
| [MonitorController.cs](MonitorController.cs) | Lists physical monitors and reads and writes VCP features through `dxva2.dll` |
| [NvApi.cs](NvApi.cs) | Builds DDC/CI packets and sends them over NVAPI raw I2C |
| [HotkeyManager.cs](HotkeyManager.cs) | System-wide hotkeys through `RegisterHotKey` and a window message hook |
| [Settings.cs](Settings.cs) | Presets and transport settings, saved as JSON in `%AppData%` |
| [MainWindow.xaml.cs](MainWindow.xaml.cs) | The window: preset list, hotkey capture, tray icon, Read and Test |
| [Autostart.cs](Autostart.cs) | The optional per-user startup entry |
| [App.xaml.cs](App.xaml.cs) | Startup, single-instance check and the command-line switch |

The window uses WPF's built-in Fluent theme (`ThemeMode="System"`, still marked experimental in .NET), so it follows the Windows light or dark setting.

## Known limits

- The NVAPI path sends the write to every connected display on every NVIDIA GPU instead of one. In practice other monitors ignore a register they don't implement. If that ever causes trouble, matching the display by EDID would fix it.
- The NVAPI transport only works with NVIDIA GPUs. On AMD or Intel, a monitor that needs the `0x50` channel can't be driven yet.
