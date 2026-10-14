namespace MediaBridge;

/// <summary>
/// The settings page at http://127.0.0.1:48902/ (Linux has no tray menu, and it's handy on Windows too).
/// It talks to MediaBridge with the same JSON commands mods use, via POST /api/cmd.
/// </summary>
static class ControlPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>MediaBridge</title>
<style>
  :root { --bg:#f6f7f8; --card:#fff; --text:#16181d; --muted:#5f6670; --line:#d9dde3; --accent:#1eb96e; }
  @media (prefers-color-scheme: dark) { :root { --bg:#14161a; --card:#1d2026; --text:#eceef2; --muted:#9aa1ad; --line:#2e333b; } }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--text); font:15px/1.45 system-ui, sans-serif; }
  main { max-width:520px; margin:0 auto; padding:16px; display:grid; gap:12px; }
  h1 { font-size:18px; margin:4px 0; display:flex; align-items:center; gap:8px; }
  h1 i { width:12px; height:12px; border-radius:50%; background:var(--accent); display:inline-block; }
  section { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:14px; display:grid; gap:10px; }
  h2 { font-size:12px; text-transform:uppercase; letter-spacing:.06em; color:var(--muted); margin:0; }
  .song { display:grid; grid-template-columns:72px 1fr; gap:12px; align-items:center; }
  .song img { width:72px; height:72px; border-radius:8px; object-fit:cover; background:var(--line); }
  .title { font-weight:600; font-size:16px; overflow-wrap:anywhere; }
  .muted { color:var(--muted); font-size:13px; overflow-wrap:anywhere; }
  .buttons { display:flex; gap:8px; }
  button { flex:1; font:inherit; padding:10px; border-radius:8px; border:1px solid var(--line); background:var(--bg); color:var(--text); cursor:pointer; }
  button:hover { border-color:var(--accent); }
  select, input[type=text] { width:100%; font:inherit; padding:8px; border-radius:8px; border:1px solid var(--line); background:var(--bg); color:var(--text); }
  input[type=range] { width:100%; accent-color:var(--accent); }
  label.check { display:flex; gap:8px; align-items:center; }
  code { font-size:13px; overflow-wrap:anywhere; }
  .row { display:flex; justify-content:space-between; gap:8px; }
  #err { color:#d9534f; font-size:13px; min-height:1em; }
  #updateLink { color:var(--accent); font-weight:600; }
</style>
</head>
<body>
<main>
  <h1><i></i> MediaBridge <span class="muted" id="version"></span></h1>

  <section id="updates" hidden>
    <h2>Update available</h2>
    <div class="muted" id="updateText"></div>
    <a id="updateLink" href="https://github.com/AlexUnknown37/MediaBridge/releases" target="_blank" rel="noopener noreferrer">View release and download</a>
  </section>

  <section>
    <h2>Now playing</h2>
    <div class="song">
      <img id="art" alt="">
      <div><div class="title" id="title">Nothing playing</div><div class="muted" id="artist"></div><div class="muted" id="app"></div></div>
    </div>
    <div class="buttons">
      <button onclick="cmd({cmd:'previous'})">⏮ Previous</button>
      <button onclick="cmd({cmd:'play_pause'})" id="pp">⏯ Play/Pause</button>
      <button onclick="cmd({cmd:'next'})">Next ⏭</button>
    </div>
  </section>

  <section>
    <h2>Listen to</h2>
    <select id="device" onchange="cmd({cmd:'select_device', deviceId:this.value})"></select>
    <div class="row"><span>Volume</span><span id="volText"></span></div>
    <input type="range" id="vol" min="0" max="100" oninput="volDrag(this.value)">
    <label class="check"><input type="checkbox" id="mute" onchange="cmd({cmd:'mute', value:this.checked})"> Mute</label>
  </section>

  <section id="linux" hidden>
    <h2>Linux extras</h2>
    <label class="check"><input type="checkbox" id="route" onchange="saveRoute()"> Send these apps here automatically:</label>
    <input type="text" id="routeApps" placeholder="spotify" onchange="saveRoute()">
    <label class="check"><input type="checkbox" id="listen" onchange="cmd({cmd:'set_listen', value:this.checked})"> Also play it on my speakers</label>
  </section>

  <section>
    <h2>Radio stream for games</h2>
    <code id="radio"></code>
    <div class="muted">Point a game's internet radio (or an .m3u8 playlist) at this link.</div>
  </section>
  <div id="err"></div>
</main>
<script>
let artId = null, dragging = 0, volTimer = null;
async function cmd(c) {
  try {
    const r = await fetch('/api/cmd', { method:'POST', headers:{'Content-Type':'application/json','X-MediaBridge':'1'}, body:JSON.stringify(c) });
    const j = await r.json();
    document.getElementById('err').textContent = j.ok ? '' : (j.message || j.error || '');
    setTimeout(refresh, 250);
    return j;
  } catch (e) { document.getElementById('err').textContent = 'MediaBridge is not running.'; }
}
function volDrag(v) {
  dragging = Date.now();
  document.getElementById('volText').textContent = v + '%';
  clearTimeout(volTimer);
  volTimer = setTimeout(() => cmd({cmd:'set_volume', value:v/100}), 80);
}
function saveRoute() {
  const on = document.getElementById('route').checked;
  const apps = document.getElementById('routeApps').value.split(',').map(s => s.trim()).filter(Boolean);
  cmd({cmd:'set_route_apps', apps: on ? (apps.length ? apps : ['spotify']) : []});
}
async function refresh() {
  try {
    const s = await (await fetch('/api/state')).json();
    const m = s.media, v = s.volume;
    document.getElementById('version').textContent = s.updates ? 'v' + s.updates.currentVersion : '';
    const updates = s.updates;
    document.getElementById('updates').hidden = !updates?.available;
    if (updates?.available) {
      document.getElementById('updateText').textContent = (updates.releaseName || 'A new version is available') + ' (v' + updates.latestVersion + ').';
      document.getElementById('updateLink').href = updates.releaseUrl;
    }
    document.getElementById('title').textContent = m.title || 'Nothing playing';
    document.getElementById('artist').textContent = [m.artist, m.album].filter(Boolean).join(' · ');
    document.getElementById('app').textContent = m.app ? (m.app + ' — ' + m.status) : '';
    if (m.artworkId !== artId) { artId = m.artworkId; document.getElementById('art').src = artId ? '/artwork?' + artId : ''; }
    if (Date.now() - dragging > 1500) {
      document.getElementById('vol').value = Math.round(v.volume * 100);
      document.getElementById('volText').textContent = Math.round(v.volume * 100) + '%';
    }
    document.getElementById('mute').checked = v.muted;
    const sel = document.getElementById('device');
    if (document.activeElement !== sel) {
      sel.innerHTML = '';
      for (const kind of ['output', 'input']) {
        const g = document.createElement('optgroup'); g.label = kind === 'output' ? 'Outputs' : 'Inputs';
        for (const d of s.devices.filter(d => d.kind === kind)) {
          const o = document.createElement('option'); o.value = d.id; o.selected = d.selected;
          o.textContent = d.name + (d.isVirtualCable && kind === 'output' ? '  (recommended)' : ''); g.appendChild(o);
        }
        sel.appendChild(g);
      }
    }
    document.getElementById('radio').textContent = s.radioUrl;
    if (s.linux) {
      document.getElementById('linux').hidden = false;
      document.getElementById('listen').checked = s.linux.listen;
      document.getElementById('route').checked = s.linux.routeApps.length > 0;
      const ra = document.getElementById('routeApps');
      if (document.activeElement !== ra && s.linux.routeApps.length) ra.value = s.linux.routeApps.join(', ');
    }
    document.getElementById('err').textContent = '';
  } catch (e) { document.getElementById('err').textContent = 'MediaBridge is not running.'; }
}
refresh(); setInterval(refresh, 1000);
</script>
</body>
</html>
""";
}
