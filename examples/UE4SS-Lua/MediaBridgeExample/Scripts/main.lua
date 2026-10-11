-- MediaBridge example for UE4SS Lua (works in any Unreal Engine game UE4SS supports).
--
-- UE4SS Lua can't open network sockets, so it uses MediaBridge's script bridge instead:
--   commands  -> write a line to the named pipe \\.\pipe\MediaBridge
--   song info <- read %LOCALAPPDATA%\MediaBridge\nowplaying.txt (rewritten only when the song changes)
--
-- Keys in this example:  F5 = previous   F6 = play/pause   F7 = next
-- It prints every song change to the UE4SS console.
--
-- Real-world use: the Pacific Drive "MediaBridge Radio" mod hooks the car radio's buttons
-- and puts the song name on the radio screen with exactly these two functions.

local TAG = "[MediaBridgeExample] "

-- Long brackets ([[ ]]) keep the backslashes as-is.
local PIPE = [[\\.\pipe\MediaBridge]]
-- Forward slashes work fine on Windows and avoid escaping.
local NOWPLAYING = (os.getenv("LOCALAPPDATA") or "") .. "/MediaBridge/nowplaying.txt"

-- ---------- send a command ----------
-- Commands: play pause play_pause stop next previous volume_up volume_down toggle_mute
local function send(cmd)
  local f = io.open(PIPE, "w")
  if not f then
    print(TAG .. "MediaBridge isn't running\n")
    return false
  end
  f:write(cmd .. "\n")
  f:close()
  return true
end

-- ---------- read the current song ----------
-- Returns a table {title=, artist=, album=, status=, app=, seq=} or nil.
local function readNowPlaying()
  local f = io.open(NOWPLAYING, "r")
  if not f then return nil end
  local data = f:read("*a")
  f:close()
  local info = {}
  for key, value in data:gmatch("([%w_]+)=([^\r\n]*)") do info[key] = value end
  return info
end

-- ---------- keys ----------
RegisterKeyBind(Key.F5, function() send("previous") end)
RegisterKeyBind(Key.F6, function() send("play_pause") end)
RegisterKeyBind(Key.F7, function() send("next") end)

-- ---------- watch for song changes ----------
-- "seq" goes up every time the file is rewritten, so compare it instead of the whole text.
local lastSeq
LoopAsync(500, function()
  local info = readNowPlaying()
  if info and info.seq ~= lastSeq then
    lastSeq = info.seq
    if info.title ~= "" then
      print(TAG .. "Now playing: " .. info.title .. " - " .. info.artist .. " (" .. info.status .. ")\n")
      -- To show it in game, set a TextBlock from the game thread, e.g.:
      -- ExecuteInGameThread(function() myTextBlock:SetText(FText(info.title .. " - " .. info.artist)) end)
    end
  end
  return false -- keep looping
end)

print(TAG .. "loaded. F5 = previous, F6 = play/pause, F7 = next\n")
