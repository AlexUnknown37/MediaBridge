# MediaBridge 0.3.2

## What's new

- Added a red **Quit MediaBridge** button to the browser control page (`http://127.0.0.1:48902/`), with a confirmation first. On Linux and Steam Deck, where MediaBridge has no tray icon, this is now the easy way to close it.
- Fixed album art on Linux for apps installed as Flatpaks, such as Chrome on Steam Deck. Their covers are saved inside the app's sandbox, and MediaBridge now finds them there.
- Expanded the README's Linux section: how automatic routing matches app names, common app words to use, and how to route an app by hand.

## Downloads

- `MediaBridge-v0.3.2-windows-x64.zip`: Windows 10 (version 2004) or newer, and Windows 11.
- `MediaBridge-v0.3.2-linux-x64.zip`: Linux x64, including Steam Deck.

Both archives contain a single self-contained app binary; no .NET runtime installation is needed.
