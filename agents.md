# Voolime Agent Notes

## Product

Voolime is a small Windows background application that changes the volume of the currently active application instead of the global system volume.

The normal Windows volume keys keep their default behavior. Voolime only handles volume keys or mouse wheel input when the configured activation keys are held. Keyboard and mouse activation keys are configurable independently from the tray context menu and can use Shift, Control, Alt, any combination of them, or none. Selecting no modifiers disables that input mode.

## User-Facing Behavior

- The app runs in the background and exposes a tray icon.
- The tray context menu contains `Keyboard Activation Key`, `Mouse Activation Key`, `Start with Windows`, and `Exit`.
- The tray context menu contains `Start with Windows`, which toggles a fixed `Voolime.lnk` shortcut in the current user's Startup folder.
- The tray context menu contains `Open Volume Mixer`, which opens the Win32 `sndvol.exe` mixer.
- The tray context menu contains `Open Playback and Recording Devices`, which opens the legacy Control Panel Sound dialog through `mmsys.cpl`.
- The active app volume changes when the configured keyboard activation key is held while pressing Volume Up, Volume Down, or Mute.
- The active app volume changes when the configured mouse activation key is held while using the mouse wheel.
- Single key presses adjust app volume by 0.5%. Held key repeat events adjust volume by 2% after the first press.
- A compact Windows 11-style flyout appears near the bottom center of the active monitor.
- The flyout follows the Windows app theme. It uses a light background in light mode, a dark background in dark mode, and the Windows accent color for the active volume bar.

## Architecture

- `Program.cs` owns single-instance startup and starts the WPF application loop.
- `AppController.cs` wires the app together. It owns the tray icon, context menu, hotkey service, active-window resolver, audio service, and flyout.
- `AppLogger.cs` writes simple per-user logs to `%LOCALAPPDATA%\Voolime\Logs\voolime.log`.
- `StartupShortcutService.cs` resolves the Startup folder once and creates or deletes the fixed `Voolime.lnk` shortcut.
- `UpdateService.cs` checks GitHub Releases on startup and can launch a small PowerShell helper to replace the current executable after the app exits.
- `HotkeyService.cs` registers global keyboard hotkeys and installs low-level keyboard and mouse hooks. The keyboard hook catches media keys reliably when Windows does not route them through normal hotkey registration. The mouse hook handles configured modifier + mouse wheel volume changes.
- `AppSettings.cs` persists user preferences under `HKCU\Software\Voolime`.
- `ActiveWindowResolver.cs` identifies the active application from the foreground window, root window, visible child windows, process IDs, executable paths, and process names.
- `AudioSessionService.cs` enumerates Core Audio render sessions and matches them to the active app. Matching uses exact process ID, executable path, process name, audio session display name, and related renderer/helper processes in the same app directory.
- `FlyoutWindow.cs` renders the compact overlay. It reads Windows theme and accent state at display time so theme changes are picked up without restarting the app.
- `NativeMethods.cs` contains all Win32 and DWM P/Invoke declarations.
- `tools/IconBuilder` generates the multi-size `.ico` asset and preview image.

## Build And Release

Use a framework-dependent single-file publish to keep the executable small:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

The published executable is `publish\Voolime.exe`. It requires the .NET Desktop Runtime on the target machine.

Release assets are expected to include a file named `Voolime.exe`. The startup update checker uses the latest GitHub Release and that asset name.

## Language Policy

Communication with the user may happen in any language.

All user-facing application text, repository documentation, code comments, identifiers, commit messages, release notes, and any other text committed to the project must be written in English.
