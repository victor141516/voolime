# Voolime

Voolime is a tiny Windows background app that changes the volume of the app you are currently using.

- `Shift + Volume Up`: raises the active app volume by default.
- `Shift + Volume Down`: lowers the active app volume by default.
- `Shift + Mute`: toggles mute for the active app by default.
- `Control + Shift + Mouse Wheel`: raises or lowers the active app volume by default.
- Normal volume keys keep controlling the global Windows volume.
- Keyboard and mouse activation keys can be changed independently from the tray menu.
- Each activation key can use Shift, Control, Alt, any combination of them, or none. Selecting none disables that input mode.
- Single presses adjust volume by 0.5%. Holding a volume key accelerates subsequent repeat steps to 2%.
- The tray menu can create or remove a visible `Voolime.lnk` shortcut in the current user's Startup folder.
- The tray menu can open the legacy Windows Volume Mixer.
- The tray menu can open the legacy Playback and Recording Devices window.
- The app writes simple logs to `%LOCALAPPDATA%\Voolime\Logs\voolime.log`.
- The app checks GitHub Releases for updates at startup and can replace the current executable when the location is writable.

The app uses the Windows Core Audio session API and a small Windows 11-style flyout near the bottom center of the active monitor. The flyout follows the Windows light/dark app theme and uses the configured Windows accent color for the volume bar.

## Build

```powershell
dotnet build -c Release
```

## Run

```powershell
dotnet run -c Release
```

## Publish a small framework-dependent exe

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

This keeps the app small and uses the installed .NET Desktop Runtime. A fully self-contained build is possible, but it is much larger.

## Notes

Voolime matches the active audio target using several fallbacks: the active window PID, visible child-window PIDs for Windows app hosts, the executable path, the audio session display name, and related renderer/helper processes in the same application directory. This covers multiprocess apps such as Chrome, Edge, Spotify, Discord, League of Legends, and many games.

Windows prevents normal apps from inspecting or hooking some elevated/admin windows. If you want Shift+volume to work while an elevated app or game is focused, run Voolime elevated too.
