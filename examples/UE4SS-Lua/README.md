# MediaBridge example: UE4SS Lua (Unreal Engine games)

A small UE4SS Lua mod that controls your music and prints each song change. It works in any Unreal Engine game that UE4SS supports.

UE4SS Lua can't open network sockets, so it uses MediaBridge's **script bridge** instead of the TCP ports:

| | How |
|---|---|
| Send commands | write `next\n` etc. to the named pipe `\\.\pipe\MediaBridge` |
| Read the song | read `%LOCALAPPDATA%\MediaBridge\nowplaying.txt` (`title=`, `artist=`, `album=`, `status=`, `app=`, `seq=`) |

See [docs/PROTOCOL.md](../../docs/PROTOCOL.md) for the details.

## Try it

1. Install UE4SS for your game (put `dwmapi.dll` and the `ue4ss` folder next to the game's `-Shipping.exe`).
2. Copy the `MediaBridgeExample` folder into `ue4ss\Mods\`.
3. Add `MediaBridgeExample : 1` to `ue4ss\Mods\mods.txt` (above `Keybinds : 1`).
4. Start MediaBridge, play some music, start the game.
5. Press **F5 / F6 / F7** for previous / play-pause / next. Song changes show in the UE4SS console.

## Music in the game world

If the game has an internet-radio feature (many Unreal games play streams through Windows Media Foundation), point it at
`http://127.0.0.1:48902/MediaBridge.wav`. The game plays the music with its own 3D audio, and your Lua mod only handles the
buttons and song info. The WAV stream starts about twice as fast as the MP3 one (~3 s instead of ~6 s) in Media Foundation players.

## Tips

- Write Windows paths in Lua with long brackets (`[[\\.\pipe\MediaBridge]]`) or forward slashes. A normal string like `"\M"` is an error in Lua.
- Change game objects only inside `ExecuteInGameThread(...)`.
- To hook a game's own buttons, find the functions with UE4SS Live View, then `RegisterHook("/Game/...:FunctionName", function() send("next") end)`.

## Real-world example

**[MediaBridge Radio for Pacific Drive](https://github.com/AlexUnknown37/PacificDrive-MediaBridgeRadio)**: the car radio's channel buttons skip songs, and the radio screen shows "Title - Artist".
It uses the game's custom radio with the WAV link and these same two functions.
