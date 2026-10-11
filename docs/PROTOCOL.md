# MediaBridge protocol (for mod makers)

MediaBridge is a small tray app that lets a game mod:

- control music (play, pause, next, previous)
- read what is playing (title, artist, album, cover art, position)
- change the volume of the audio device the user picked (e.g. VB-Cable) — perfect for an in-game volume slider
- receive the live audio of that device, so the mod can play it through the game's own 3D audio

Everything happens over two TCP ports on `127.0.0.1`. Nothing is reachable from other computers.

| Port  | What                                   |
|-------|----------------------------------------|
| 48900 | Control: JSON text, one message per line, both directions |
| 48901 | Audio: 16-byte header, then raw float32 samples forever |
| 48902 | Radio: the same audio as an MP3 internet-radio stream (see bottom) |

Ports can be changed by the user; the current ones are in the discovery info (below).

---

## Finding and starting MediaBridge

1. Try to connect to `127.0.0.1:48900`. If it works, you're done.
2. If not, MediaBridge may be installed but not running. Read either:
   - registry `HKEY_CURRENT_USER\Software\MediaBridge` → `InstallPath`, `ControlPort`, `AudioPort`, `Version`
   - or the file `%LOCALAPPDATA%\MediaBridge\bridge.json` → `installPath`, `controlPort`, `audioPort`, `version`, `protocol`

   Start `InstallPath`, wait ~1 second, connect again. (Starting it twice is harmless — the second copy exits.)
   On Linux, see "Linux notes" below.
3. If neither exists, it isn't installed. Show the player a message with the download link.
   **Please don't bundle or silently install MediaBridge** — one shared copy serves every mod.

---

## Control channel (port 48900)

Every message is one JSON object followed by `\n` (UTF-8).

### When you connect

MediaBridge immediately sends:

```json
{"type":"hello","name":"MediaBridge","version":"0.1.0","protocol":1,"audioPort":48901}
{"type":"event","event":"media", ...}
{"type":"event","event":"volume", ...}
{"type":"event","event":"device", ...}
```

Optionally introduce yourself (shown in MediaBridge's log):

```json
{"cmd":"hello","name":"My Cool Mod"}
```

### Commands you can send

Add `"id"` to any command (number or string) and the reply will carry the same `id`, so you can match them up.

| Command | Extra fields | What it does |
|---|---|---|
| `play` | | Resume music |
| `pause` | | Pause music |
| `play_pause` | | Toggle |
| `stop` | | Stop |
| `next` | | Next track |
| `previous` | | Previous track |
| `set_volume` | `"value"`: 0.0–1.0 | Set the picked device's Windows volume. **Use this for volume sliders.** |
| `volume_up` / `volume_down` | `"step"`: optional, default 0.05 | Nudge the volume |
| `mute` | `"value"`: true/false (default true) | Mute / unmute |
| `unmute` | | Unmute |
| `toggle_mute` | | Toggle mute |
| `get_state` | | Reply includes `media`, `volume`, `device` |
| `get_artwork` | | Reply includes `artworkId`, `mime` (e.g. `image/png`), `data` (base64) |
| `list_devices` | | Reply includes `devices`: `[{id, name, kind, selected, isVirtualCable}]` |
| `select_device` | `"deviceId"` | Switch device (saved as the user's choice). Usually the user does this from the tray menu. |
| `ping` | | Replies ok |

Replies look like:

```json
{"type":"reply","id":7,"ok":true}
{"type":"reply","id":8,"ok":false,"error":"media_unavailable","message":"No media app is open, or it refused the command."}
```

Error codes: `bad_json`, `bad_args`, `unknown_cmd`, `media_unavailable`, `no_device`, `no_artwork`, `bad_device`, `error`.

### Events MediaBridge sends you

You get these automatically whenever something changes. You don't need to poll.

**media** — song or play state changed

```json
{"type":"event","event":"media",
 "app":"Spotify.exe","title":"PARADE","artist":"Lunar949","album":"PARADE",
 "status":"playing",              // playing | paused | stopped | changing | none
 "canPlay":false,"canPause":true,"canNext":true,"canPrevious":true,
 "position":52.67,"duration":220.76,   // seconds
 "positionUpdatedAt":1791691113845,    // unix ms when "position" was measured
 "artworkId":"b55971053cd35424"}      // changes when the cover changes; null if none
```

Live position while playing = `position + (nowUnixMs - positionUpdatedAt) / 1000`.
When `artworkId` changes, send `get_artwork` to fetch the new cover.

**volume** — the picked device's volume changed (by a mod, the tray menu, or the Windows mixer)

```json
{"type":"event","event":"volume","volume":0.65,"muted":false,"deviceId":"{0.0.0.00000000}.{...}"}
```

Use this to keep your slider in sync. `volume` is the same 0–1 value the Windows mixer shows.

**device** — the user picked a different device

```json
{"type":"event","event":"device","id":"...","name":"CABLE Input (VB-Audio Virtual Cable)",
 "kind":"output","isVirtualCable":true,"sampleRate":48000,"channels":2,"capturing":true}
```

When this happens, your audio connection is closed. Reconnect to read the new header.

---

## Audio channel (port 48901)

Connect and read. You never send anything.

**Header (16 bytes, little-endian):**

| Bytes | Type | Value |
|---|---|---|
| 0–3 | ASCII | `MBA1` |
| 4–7 | int32 | sample rate (usually 48000) |
| 8–9 | int16 | channels (always 2) |
| 10–11 | int16 | sample format: 3 = 32-bit float |
| 12–15 | | reserved (0) |

**Then:** interleaved stereo float32 samples (`L R L R ...`), live, forever.

Things to know:

- **For 3D sound, mix to mono:** `(L + R) / 2`, then play it from a positional sound source in your game.
- **Silence = no data.** When nothing is playing, Windows sends nothing, so treat "buffer empty" as silence.
- **Keep a small buffer.** Aim for ~100 ms buffered. If more than ~300 ms piles up (your game's audio clock runs slightly slower than the device's), drop the oldest samples. Otherwise latency slowly grows.
- If the connection closes, wait a second and reconnect. That happens when the user switches device.
- The audio already has the device volume applied. `set_volume` makes the stream quieter or louder too.

See `examples/Unity/MediaBridgeClient.cs` for a full working client.

---

## Radio stream (port 48902): the no-code audio option

```
http://127.0.0.1:48902/MediaBridge.wav
http://127.0.0.1:48902/MediaBridge.mp3
```

The same audio as a live internet-radio stream, in two formats:

- **WAV** (uncompressed 16-bit PCM, 48 kHz stereo, ~190 KB/s). Use this one if the game plays streams through
  Windows Media Foundation (most Unreal Engine games): it starts after ~3 s, versus ~6 s for MP3.
- **MP3** (192 kbps, encoded with Windows' built-in encoder, only while someone listens), for players that don't take WAV streams.

If your game already has an **internet radio** feature, point it at one of these. The game then plays the music with its own 3D audio, and your mod only handles buttons and song info.
Expect some delay, as with any internet radio: it's the game's player buffering, not MediaBridge.

MediaBridge also writes a ready-made playlist (WAV link) to `%LOCALAPPDATA%\MediaBridge\MediaBridge.m3u8`. The URLs are also in `bridge.json` (`radioWavUrl`, `radioUrl`) and the registry (`RadioWavUrl`, `RadioUrl`).
Example: Pacific Drive plays it when `MediaBridge.m3u8` is copied into `%LOCALAPPDATA%\PenDriverPro\Custom\Radio\`.

---

## Script bridge: for mods that can't open sockets (e.g. UE4SS Lua)

Some mod loaders' scripting languages can open files but not TCP sockets. MediaBridge offers a file-based way in:

**Commands:** open the named pipe `\\.\pipe\MediaBridge` for writing and write one command per line.
Commands: `play`, `pause`, `play_pause`, `stop`, `next`, `previous`, `volume_up`, `volume_down`, `toggle_mute`.
There are no replies. If the pipe can't be opened, MediaBridge isn't running.

```lua
local f = io.open([[\\.\pipe\MediaBridge]], "w")
if f then f:write("next\n"); f:close() end
```

**Song info:** `%LOCALAPPDATA%\MediaBridge\nowplaying.txt`, one `key=value` per line:
`title`, `artist`, `album`, `status`, `app`, `seq`. It's rewritten only when the song or play state changes,
and `seq` goes up by one every time, so poll it (e.g. every 250 ms) and compare `seq`.

**On Linux / Steam Deck (games running under Proton):** a Windows game can't reach a Linux pipe, so use the
shared folder `/tmp/MediaBridge`, which the game sees as `Z:\tmp\MediaBridge`:
- **commands:** append lines to `Z:\tmp\MediaBridge\commands.txt`. MediaBridge picks up new lines within 100 ms.
- **song info:** read `Z:\tmp\MediaBridge\nowplaying.txt` (same format).

Try the pipe first and fall back to the folder, and the same mod works on both systems:

```lua
local f = io.open([[\\.\pipe\MediaBridge]], "w") or io.open("Z:/tmp/MediaBridge/commands.txt", "a")
if f then f:write("next\n"); f:close() end
```

---

## Linux notes

- **Same protocol, same ports.** Native Linux mods and Windows games under Proton both connect to `127.0.0.1:48900` / `48901` / `48902`.
- **Discovery:** `bridge.json` is in `~/.local/share/MediaBridge/` and `/tmp/MediaBridge/` (Proton: `Z:\tmp\MediaBridge\bridge.json`). It has `"os": "linux"`. There's no registry entry.
  If it isn't running, a Proton game can't start it for the player. Ask them to run `MediaBridge --install` once instead.
- **Radio:** Linux has no built-in MP3 encoder, so both radio URLs stream WAV.
- **Device IDs** look like `sink:MediaBridge` (outputs) or `source:alsa_input...` (inputs).
- **Extra commands (Linux only; other systems reply `"error":"unsupported"`):**

| Command | Extra fields | What it does |
|---|---|---|
| `set_route_apps` | `"apps"`: e.g. `["spotify"]` | Send these apps into the picked output automatically, including when they start later. `[]` sends them back to the speakers. |
| `set_listen` | `"value"`: true/false | Also play the picked output on the normal speakers, to hear the music outside games |

`get_state` replies also include `"linux": {"routeApps": [...], "listen": false}`.

---

## Control page (port 48902)

`http://127.0.0.1:48902/` is a small settings page for players: song, buttons, volume, device picker (and the Linux extras).
It uses `GET /api/state` and `POST /api/cmd`. The body is any control-channel command as JSON, and the request must
include the header `X-MediaBridge: 1`, so other websites can't send commands. Cover art is at `/artwork`.
