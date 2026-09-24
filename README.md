# Star Citizen Anti AFK

A tiny, portable anti-idle utility for Windows 10/11. When you walk away from your keyboard, it waits for a
time you choose, optionally brings **StarCitizen.exe** to the front, and then sends a keypress or a mouse
wiggle at an interval you choose. The moment you touch your keyboard or mouse again, it stands down and goes
back to waiting.

Made by **C0P3Y** | **Icarus Interstellar Inc.**

## Features

- Single small `.exe` (~60 KB). No installer, no runtime to install (uses the .NET Framework built into Windows).
- No registry entries. Settings are stored in `StarCitizenAntiAFK.ini` next to the exe
  (or `%LOCALAPPDATA%\StarCitizenAntiAFK\` if that folder is read-only). Delete the exe and the ini to uninstall.
- Configurable idle time (minutes and seconds) before AFK input begins.
- Configurable interval between AFK inputs.
- Input type switch: **Keyboard** (any key you pick) or **Mouse wiggle** (centers the cursor, moves 10 px right, then back).
- Global start/stop hotkey (default `Ctrl + Shift + F9`) that works while a game has focus.
- **Switch to Star Citizen first** toggle: before each AFK input the app makes `StarCitizen.exe` the active
  window, so inputs land in the game even if you alt-tabbed away. If the game isn't running, nothing is switched
  and input goes to the current window, so the app still works for other games.
- Runs in the background with a tray icon (grey = stopped, amber = waiting, green = AFK input active).
- Dark, modern UI with high-DPI support.

## Usage

1. Run `StarCitizenAntiAFK.exe`.
2. Set the idle time, interval, input type and (optionally) the key to press.
3. Press **Start** or your hotkey. The app now runs on its own.
4. Walk away. After the idle time it starts sending input; touch your keyboard or mouse and it goes back to waiting.

Pick an AFK key that is harmless in your game (the default is `Space`, which usually jumps).

## Build from source

You only need Windows. The C# compiler that ships with the .NET Framework is enough:

```
build.bat
```

This produces `StarCitizenAntiAFK.exe` in the repo root. Equivalent manual command:

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /out:StarCitizenAntiAFK.exe ^
  /win32manifest:src\app.manifest /win32icon:src\app.ico ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\StarCitizenAntiAFK.cs
```

The source is written in C# 5 style on purpose so this built-in compiler can build it. Visual Studio, the .NET SDK
(targeting `net48`) or Mono's `mcs` also work.

## How it works

- **Idle detection:** polls `GetLastInputInfo` every 250 ms. No global keyboard/mouse hooks are installed.
- **Ignoring its own input:** after each injected input, the app records the resulting last-input timestamp.
  A later timestamp that doesn't match means something else (you) produced input, so it returns to waiting.
- **Sending input:** `SendInput` with hardware scan codes for keys, and a relative mouse move for the wiggle.
- **Focusing the game:** finds the `StarCitizen` process's main window and uses the standard
  `AttachThreadInput` + `SetForegroundWindow` approach, with `SwitchToThisWindow` as a fallback. If the game is
  running but can't be focused, that round of input is skipped and retried a few seconds later, so keys are never
  typed into an unrelated window.
- **Hotkey:** `RegisterHotKey`; the app reports if another program already owns the combination.

## Settings file

`StarCitizenAntiAFK.ini` is plain `Key=Value` text: `IdleSeconds`, `IntervalSeconds`, `Mode` (0 keyboard,
1 mouse), `KeyVk`, `HotVk`, `HotMods` (1 Alt, 2 Ctrl, 4 Shift), `Tray`, `FocusGame`. It is created by the app and
is git-ignored.

## Notes and limitations

- If the game runs as administrator, run this app as administrator too; Windows blocks input from a normal app
  to an elevated one.
- Some games ignore synthetic mouse movement when they capture the cursor. Use keyboard mode if the wiggle
  has no effect.
- Simulated input may be against the rules of some online games. Check the terms of the game you use it with and
  use this software at your own risk.
- Only one instance runs at a time.

## Project layout

```
src/StarCitizenAntiAFK.cs   application source (single file)
src/app.manifest            DPI awareness, Windows 10/11 compatibility, no elevation
src/app.ico                 application icon
build.bat                   builds the exe with the compiler built into Windows
```

## License

MIT, see [LICENSE](LICENSE).
