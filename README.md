# Dynamic Island for Windows

A Liquid Glass pill at the top-center of the screen that morphs to show the clock, battery, music controls and your Windows notifications.

`DynamicIsland.exe` is a single file of about 80 KB. It runs on Windows 10 and 11 with nothing to install, because it uses the .NET Framework 4.8 that ships with Windows.

## Use

- **Hover** the island to expand it. Move away and it collapses. **Click** also toggles it.
- **Right-click** the island, click the tray icon, or run the exe again to open **Settings**.
- Collapsed, it shows the time and battery. While music plays, it shows album art and a live equalizer.
- Expanded, it shows a large clock and date, or the music card with previous / play-pause / next and a progress bar.
- Live activities appear as brief alerts: Windows notifications, Now Playing, charger plugged in or unplugged, and low battery at 20% and 10%.

## Settings

Settings are stored in `%APPDATA%\DynamicIsland\settings.ini`.

| Setting | Notes |
|---|---|
| Launch at startup | Adds the exe's current path to `HKCU\...\Run`. If you move the exe, toggle this off and on again. |
| Expand on hover | Turn it off for click-only. |
| Hide during fullscreen games & video | Detects fullscreen D3D apps and borderless fullscreen windows on the island's monitor. |
| Liquid Glass blur | Live DWM blur clipped to the pill. **Windows 11 only.** Windows 10 gets solid dark glass instead. |
| Glass tint / Distance from top | Applied live. |
| Show Windows notifications | Needs *Settings › Privacy & security › Notifications › Let apps access your notifications*. |

## Performance

- The island renders frames only while it is morphing. At rest it does a 1-second tick, and a notification check every 2 s.
- In fullscreen games the window is hidden. All that keeps running is a 2-second fullscreen check, and the working set gets trimmed.
- Memory is trimmed after 20 s at rest.

## Build

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

This compiles with the `csc.exe` built into Windows (no SDK needed), generates the icon, and runs `DynamicIsland.exe --selftest`. The self-test checks the spring physics, settings parsing and the XAML.

Source files in `src/`:

- `Island.cs`: the window, spring animation, activities, tray and settings
- `Backdrop.cs`: the Windows 11 composition blur
- `Services.cs`: media session and notification listener
- `Program.cs`: entry point, settings, Win32 interop and the self-test
- `Island.xaml` / `Settings.xaml`: the UI
