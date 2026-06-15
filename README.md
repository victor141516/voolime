# Voolime

Voolime is a tiny Windows background app that changes the volume of the app you are currently using.

- `Shift + Volume Up`: raises the active app volume.
- `Shift + Volume Down`: lowers the active app volume.
- `Shift + Mute`: toggles mute for the active app.
- Normal volume keys keep controlling the global Windows volume.

The app uses the Windows Core Audio session API and a small Windows 11-style flyout near the bottom center of the active monitor.

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

Voolime matches the active audio target using several fallbacks: the active window PID, visible child-window PIDs for Windows app hosts, the executable path, and finally the process name for multiprocess apps such as Chrome, Edge, Spotify, Discord, and many games.

Windows prevents normal apps from inspecting or hooking some elevated/admin windows. If you want Shift+volume to work while an elevated app or game is focused, run Voolime elevated too.
