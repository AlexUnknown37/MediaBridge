# MediaBridge 0.3.4

A safety and polish update from a full review of the code. Mods do not need to be updated.

## Security

- Websites can no longer send commands (skip, volume, device) to MediaBridge behind your back.
- The control page only answers when opened as `127.0.0.1` or `localhost`.

## Fixed

- **Windows:** the tray menu showed garbled characters instead of ▶, ❚❚ and other symbols.
- **Windows:** "Copy radio stream link" in the tray now copies the faster WAV link, like the control page shows.
- **Linux / Steam Deck:** quitting MediaBridge sends auto-routed apps (like Spotify) back to your speakers, instead of leaving them silent.
- **Linux:** "Also play it on my speakers" now follows you when you pick a different device, and no longer doubles the sound after a crash.
- **Linux:** MediaBridge still starts (audio only) on systems without `busctl`, instead of crashing.
- Only `.mp3` and `.wav` addresses are the radio stream. Opening the control page no longer starts a stream by accident.
- Names with non-English letters work in the control page's commands.
- The log file can no longer grow without limit while MediaBridge runs for weeks.
- A game that disconnects while music is paused is now noticed right away.
- Script commands `volume_up` / `volume_down` use your `volumeStep` setting.
- A clear error message when `settings.json` has an invalid port number.

## Under the hood

- Fewer background checks on Linux when only one music app is open.
- Very large cover images are skipped.
- Automatic tests were added (`dotnet test tests/MediaBridge.Tests`).

## Downloads

- `MediaBridge-v0.3.4-windows-x64.zip`: Windows 10 (version 2004) or newer, and Windows 11.
- `MediaBridge-v0.3.4-linux-x64.zip`: Linux x64, including Steam Deck.

Both archives contain a single self-contained app binary; no .NET runtime installation is needed.

## Support

- Ko-fi: https://ko-fi.com/alexjustalex
- Patreon: https://patreon.com/AlexanderVazquez
- GitHub: https://github.com/AlexUnknown37
