<p align="center">
  <img src="assets/voolime-preview.png" alt="Voolime icon" width="128" height="128">
</p>

<h1 align="center">Voolime</h1>

<p align="center">
  <strong>Tiny per-app volume control for Windows.</strong>
</p>

<p align="center">
  Hold a modifier, press your volume keys, and change only the app you are using.
</p>

## Preview

> Screenshot slot reserved.
>
> Add a capture of the tray menu and the volume flyout here:
> `assets/voolime-menu-and-flyout.png`

<!--
When the screenshot is ready, replace the note above with:

![Voolime tray menu and volume flyout](assets/voolime-menu-and-flyout.png)
-->

## Tiny Trick, Big Relief

Windows volume keys are global. Voolime makes them app-aware.

## Features

- 🍋 Lime-sized. Very small by design.
- 🪟 Windows-native feel. No custom theme circus.
- 🎚️ Active app only. Leave system volume alone.
- ⌨️ Volume keys. Fast when held, precise when tapped.
- 🖱️ Mouse wheel. Use modifiers and scroll.
- 🎯 Smart targeting. Works with multiprocess apps.
- 🎮 Game-friendly. Built for real foreground windows.
- 🌗 Theme-aware. Follows Windows light and dark mode.
- 🎨 Accent-aware. Uses your Windows accent color.
- 🚀 Startup toggle. Just a visible shortcut.
- 🧰 Handy tray menu. Mixer, devices, settings, exit.
- 🪶 No installer. Put the exe wherever you like.
- 📝 Simple logs. Easy to inspect when needed.
- 🔄 Update check. Uses GitHub Releases.

## Default Controls

- `Shift + Volume Up`: raise the active app volume.
- `Shift + Volume Down`: lower the active app volume.
- `Shift + Mute`: toggle mute for the active app.
- `Control + Shift + Mouse Wheel`: raise or lower the active app volume.
- Normal volume keys still control the global Windows volume.

Keyboard and mouse activation keys can be changed independently from the tray menu. Each input mode can use Shift, Control, Alt, any combination of them, or no modifiers. Selecting no modifiers disables that input mode.

## Tray Menu

- `Open Volume Mixer`
- `Open Playback and Recording Devices`
- `Keyboard Activation Key`
- `Mouse Activation Key`
- `Start with Windows`
- `Exit`

`Start with Windows` creates or removes a visible `Voolime.lnk` shortcut in the current user's Startup folder.

## How It Works

Voolime uses the Windows Core Audio session API and a small Windows 11-style flyout near the bottom center of the active monitor. It matches the active audio target with several fallbacks: the active window process, child-window processes, executable paths, audio session names, and related helper processes in the same application directory.

That helps with Chrome, Edge, Spotify, Discord, League of Legends, and many games.

## Logs

Logs are written to:

```text
%LOCALAPPDATA%\Voolime\Logs\voolime.log
```

## Build

```powershell
dotnet build -c Release
```

## Run

```powershell
dotnet run -c Release
```

## Publish a Small Exe

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

This keeps the app small and uses the installed .NET Desktop Runtime. A self-contained build is possible, but it is much larger.

## Note

Windows prevents normal apps from inspecting or hooking some elevated windows. If you want modifier+volume controls to work while an elevated app or game is focused, run Voolime elevated too.
