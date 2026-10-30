# MediaBridge
[Watch Tool in Use in Pacific Drive](https://youtu.be/P02ywl0pOp0)

A tiny app for **Windows and Linux (including Steam Deck)** that lets **game mods** use your music. One file, no install needed (Windows: 4 MB `.exe` in the tray; Linux: 13 MB program that runs in the background):

- **Control it:** play, pause, next, previous
- **See it:** song title, artist, album, cover art, play position (from Windows' media controls, so it works with Spotify, browsers, etc.)
- **Set the volume** of the audio device you picked (e.g. VB-Cable), for in-game volume sliders
- **Hear it in 3D:** the mod gets the live audio and plays it from a speaker in the game world
- **Radio station:** the music is also an internet-radio stream, for games with built-in internet radio (e.g. Pacific Drive):
  `http://127.0.0.1:48902/MediaBridge.wav` (starts fastest in most games) or `http://127.0.0.1:48902/MediaBridge.mp3`
- **Script bridge:** mods that can't use network sockets (e.g. UE4SS Lua) send commands through a named pipe (Linux/Proton: a text file) and read the song from a text file
- **Control page:** <http://127.0.0.1:48902/> in any browser shows the song, buttons, volume and device picker

MediaBridge isn't a mod. It's the shared piece any mod can talk to.

---

## Setup for players

### 1. Get a virtual audio cable (recommended)

Install **VB-Cable** (free) from <https://vb-audio.com/Cable/>. It adds two devices:
- **CABLE Input**: a fake speaker that apps can play into
- **CABLE Output**: the other end of the cable

### 2. Send your music app into the cable

- **Spotify:** Settings → Playback → Output → Device → **CABLE Input**
- **Any other app:** Windows Settings → System → Sound → *App volume and device preferences* → set the app's Output to **CABLE Input**

Now only your music goes into the cable. Game sounds and Discord stay separate.

### 3. Run MediaBridge

Double-click `MediaBridge.exe`. A green music-note icon appears in the tray (bottom-right, maybe under the `^` arrow). The tray menu shows the installed version and reports when a newer packaged GitHub release is available. The control page also displays an update link.
Click it to:
- check what's playing, and play/pause/skip
- **Listen to:** pick the device. Choose **CABLE Input** (marked "recommended"). MediaBridge picks it automatically if it finds it.
- set the volume
- turn on **Start with Windows**

### Hearing music outside the game

Because the music now goes into the cable, you won't hear it on your headphones unless a mod plays it. To hear it anyway:
Control Panel → Sound → **Recording** tab → **CABLE Output** → Properties → **Listen** → tick *Listen to this device* and pick your headphones.
(Turn this off while playing a modded game, or you'll hear the music twice.)

## Setup on Linux / Steam Deck

Linux needs no VB-Cable: MediaBridge creates its own virtual speaker called **MediaBridge**.

1. Copy the `MediaBridge` program (Linux build) somewhere permanent, e.g. `~/Applications/MediaBridge/`.
   On the Steam Deck, do this in **Desktop Mode**.
2. Open a terminal (Konsole) in that folder and run:
   ```bash
   chmod +x MediaBridge && ./MediaBridge --install
   ```
   This starts MediaBridge now and every time you log in, in Desktop Mode and Game Mode alike. No sudo needed.
   (`./MediaBridge --uninstall` removes it; `./MediaBridge --status` shows whether it's running.)
3. Open <http://127.0.0.1:48902/> in a browser:
   - **Listen to:** pick **MediaBridge**. It's picked automatically.
   - **Send these apps here automatically:** tick it and type `spotify` (or more, e.g. `spotify, firefox`; see [the list below](#sending-apps-to-mediabridge-automatically)). From then on, those apps play into MediaBridge by themselves, even in Game Mode.
   - **Also play it on my speakers:** tick it if you want to hear the music outside games.

### Sending apps to MediaBridge automatically

The **Send these apps here automatically** box takes words, separated by commas (e.g. `spotify, firefox`). It doesn't need an app's exact name.

When an app starts playing sound, it opens an audio stream with a few names on it (app name, program, and Flatpak ID if it has one). For Chrome that's `Google Chrome`, `chrome` and `com.google.Chrome`. If **any of your words appears anywhere in those names** (upper/lower case doesn't matter), MediaBridge moves that app's sound into the MediaBridge speaker. It checks when it starts, when you change the list, and every time an app starts a new sound.

Common music apps and the word to type:

| App | Word |
|---|---|
| Spotify | `spotify` |
| Firefox (YouTube Music, SoundCloud, ...) | `firefox` |
| Google Chrome | `chrome` |
| Brave | `brave` |
| Microsoft Edge | `msedge` |
| Vivaldi | `vivaldi` |
| Opera | `opera` |
| VLC | `vlc` |
| Strawberry | `strawberry` |
| Elisa | `elisa` |
| Rhythmbox | `rhythmbox` |
| Audacious | `audacious` |
| Amberol | `amberol` |
| Tidal (tidal-hifi) | `tidal` |
| Plexamp | `plexamp` |
| Cider (Apple Music) | `cider` |

**Finding the word for another app:** make the app play something (it has no sound stream until it does), then run:

```bash
pactl list sink-inputs | grep -E "application.name|application.process.binary|portal.app_id"
```

Pick a word that only that app has. Usually its name works best.

**Don't use a word that's too general.** It matches anywhere, so it can grab apps you didn't mean. For example `chromium` also matches Steam's own interface (`steamwebhelper` is built on Chromium), which sends Steam's sounds into the game. Check that your word doesn't also appear in Steam's, Discord's or the game's lines. If something ends up in the wrong place, edit or untick the list on the control page and the apps go back to your speakers.

### Sending an app to MediaBridge by hand

You can also skip the list and move an app yourself. Start playing music in the app first (it only shows up while it's making sound), then pick one way:

- **KDE desktop (Steam Deck Desktop Mode):** click the speaker icon in the taskbar → **Applications** tab → next to your music app, pick **MediaBridge** as the output.
  Or: System Settings → Sound → **Applications** → your app → **MediaBridge**.
- **GNOME and other desktops:** install **pavucontrol** (Volume Control) → **Playback** tab → next to your music app, pick **MediaBridge**.
- **Terminal:** find the app's stream number, then move it:
  ```bash
  pactl list short sink-inputs
  pactl move-sink-input <number> MediaBridge
  ```
  The number changes every time the app starts a new sound, so this is mostly for testing.

To undo, pick your speakers or headphones again the same way.
Your desktop usually remembers the choice for that app next time, but not always, and there's no app picker in Game Mode. For music you play while gaming, the automatic list above is more reliable.

Needs: PipeWire or PulseAudio (`pactl`, `pacat`) and systemd (`busctl`). SteamOS and most desktops have all of these.
Music apps are found through MPRIS, the standard Linux media controls (Spotify, Firefox, Chrome, VLC, ...).

**Windows games under Proton:** mods inside the game reach MediaBridge at `127.0.0.1` exactly like on Windows.
Script-bridge mods use `Z:\tmp\MediaBridge\commands.txt` and `Z:\tmp\MediaBridge\nowplaying.txt` instead of the pipe.
The UE4SS Lua example handles both.

---

## For mod makers

- Protocol: [docs/PROTOCOL.md](docs/PROTOCOL.md)
- Ready-to-use Unity / BepInEx / MelonLoader client: [examples/Unity/MediaBridgeClient.cs](examples/Unity/MediaBridgeClient.cs)
- Unreal Engine games with UE4SS (Lua): [examples/UE4SS-Lua](examples/UE4SS-Lua)
- A complete real mod: [MediaBridge Radio for Pacific Drive](https://github.com/AlexUnknown37/PacificDrive-MediaBridgeRadio) (UE4SS Lua, Windows and Steam Deck)

The short version: connect to `127.0.0.1:48900`, send `{"cmd":"play_pause"}` lines, and read event lines. Audio is raw float32 on port `48901`.

No sockets (e.g. UE4SS Lua)? Write `next` to the named pipe `\\.\pipe\MediaBridge`, and read the song from `%LOCALAPPDATA%\MediaBridge\nowplaying.txt`. On Linux/Proton: append `next` to `Z:\tmp\MediaBridge\commands.txt` and read `Z:\tmp\MediaBridge\nowplaying.txt`.

Game has internet radio? Point it at `http://127.0.0.1:48902/MediaBridge.wav` and the game plays the music with its own 3D audio.

**Mods using MediaBridge:** [MediaBridge Radio for Pacific Drive](https://github.com/AlexUnknown37/PacificDrive-MediaBridgeRadio): plays your music on the car radio, the radio's buttons skip songs, and the radio shows the song name. Works on Windows and Steam Deck.

Please don't bundle MediaBridge inside your mod. Detect it (see PROTOCOL.md), start it if it's installed, and otherwise link players to the download.

---

## Files MediaBridge writes

| Where | What |
|---|---|
| `%LOCALAPPDATA%\MediaBridge\settings.json` | picked device, ports, volume step |
| `%LOCALAPPDATA%\MediaBridge\bridge.json` | where the .exe is, so mods can start it |
| `%LOCALAPPDATA%\MediaBridge\MediaBridge.m3u8` | radio playlist (WAV link) to copy into games with internet radio |
| `%LOCALAPPDATA%\MediaBridge\nowplaying.txt` | current song, for mods that read files (script bridge) |
| `%LOCALAPPDATA%\MediaBridge\log.txt` | log of the current run (`log.txt.old` = previous run) |
| `HKCU\Software\MediaBridge` | same info as bridge.json |
| `HKCU\...\CurrentVersion\Run\MediaBridge` | only if "Start with Windows" is on |

On Linux the same files are in `~/.local/share/MediaBridge/`. `bridge.json`, `nowplaying.txt`, `commands.txt` and `MediaBridge.m3u8` are also in `/tmp/MediaBridge/`, which Proton games see as `Z:\tmp\MediaBridge`. Auto-start: `~/.config/systemd/user/mediabridge.service`.

## Requirements

- Windows 10 version 2004 or newer, or Windows 11. 64-bit.
- Linux x64 with PipeWire or PulseAudio and systemd (SteamOS 3, Fedora, Ubuntu, Arch, ...).

---

## Building from source

Needs the .NET 10 SDK, plus Visual Studio's "Desktop development with C++" tools for the native build.

```bash
dotnet publish src/MediaBridge -f net10.0-windows10.0.19041.0 -c Release -o publish/windows
```

```bash
dotnet publish src/MediaBridge -f net10.0 -r linux-x64 -c Release -o publish/linux
```

This gives `publish/windows/MediaBridge.exe` (~4 MB native) and `publish/linux/MediaBridge` (~13 MB single file). Neither needs .NET installed to run. The Linux build can be made on Windows.

**Test client:** `tools/TestClient` pretends to be a mod.

```bash
dotnet run --project tools/TestClient
```

Then type `play_pause`, `next`, `vol 0.5`, `devices`, `select 2`, `art`, `state`.
`dotnet run --project tools/TestClient -- --audio 5` shows the live audio level for 5 seconds.

## Project layout

```
src/MediaBridge/
  Program.cs          startup, single-instance check, discovery info; picks the Windows or Linux parts
  Platform.cs         the two jobs each OS does its own way (media controls, audio)
  MediaController.cs  Windows media controls: play/pause/skip + song info + cover art
  AudioHub.cs         Windows: the picked device, recording it + its volume
  Linux/              Linux versions: MPRIS media controls, PipeWire/PulseAudio audio, --install
  ControlPage.cs      the browser control page at http://127.0.0.1:48902/
  BridgeServer.cs     the two local ports mods connect to
  RadioServer.cs      the WAV and MP3 internet-radio streams (port 48902)
  ScriptBridge.cs     named pipe (Linux: commands.txt) + nowplaying.txt, for mods without sockets
  TrayApp.cs          Windows tray icon and menu
  Win32.cs            raw Windows calls used by the tray
  Settings.cs         settings file + log
tools/TestClient/     fake mod for testing
examples/Unity/       drop-in client for Unity mods
examples/UE4SS-Lua/   example mod for Unreal Engine games (UE4SS Lua)
docs/PROTOCOL.md      everything a mod needs to know
```

## Support

If you enjoy this project, you can support me here:
- Ko-fi: [https://ko-fi.com/alexjustalex](https://ko-fi.com/alexjustalex)
- Patreon: [https://patreon.com/AlexanderVazquez](https://patreon.com/AlexanderVazquez)
- GitHub: [https://github.com/AlexUnknown37](https://github.com/AlexUnknown37)
