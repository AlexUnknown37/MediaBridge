using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using static MediaBridge.Win32;

namespace MediaBridge;

/// <summary>The system-tray icon and its menu. This is the only UI MediaBridge has.</summary>
sealed unsafe class TrayApp
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const uint WM_TRAY = WM_APP + 1;
    const uint WM_RUN_QUEUED = WM_APP + 2;

    static TrayApp? _instance; // the window procedure is static, so it finds us through this

    readonly Settings _settings;
    readonly MediaController _media;
    readonly AudioHub _audio;
    readonly BridgeServer _server;
    readonly RadioServer _radio;
    readonly ConcurrentQueue<Action> _queue = new();
    readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    nint _hwnd, _icon;

    public TrayApp(Settings settings, MediaController media, AudioHub audio, BridgeServer server, RadioServer radio)
    {
        _settings = settings;
        _media = media;
        _audio = audio;
        _server = server;
        _radio = radio;
        _instance = this;

        _media.Changed += _ => OnUi(UpdateTooltip);
        _audio.VolumeChanged += _ => OnUi(UpdateTooltip);
        _server.ClientsChanged += () => OnUi(UpdateTooltip);
        _server.UserDeviceChange += OnUi;
        _radio.ListenersChanged += () => OnUi(UpdateTooltip);
    }

    /// <summary>Creates the tray icon and runs until the user picks Exit.</summary>
    public void Run()
    {
        fixed (char* cls = "MediaBridgeTrayWindow")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = GetModuleHandleW(null),
                lpszClassName = cls,
            };
            RegisterClassExW(&wc);
            _hwnd = CreateWindowExW(0, cls, cls, 0, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);
        }
        _icon = MakeIcon();
        AddIcon();
        UpdateTooltip();
        PostMessageW(_hwnd, WM_RUN_QUEUED, 0, 0); // run anything queued before the window existed

        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        var data = IconData();
        Shell_NotifyIconW(NIM_DELETE, &data);
    }

    /// <summary>Runs an action on the tray's thread (events arrive from background threads).</summary>
    void OnUi(Action action)
    {
        _queue.Enqueue(action);
        if (_hwnd != 0) PostMessageW(_hwnd, WM_RUN_QUEUED, 0, 0);
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nint w, nint l)
    {
        var self = _instance;
        try
        {
            if (self != null)
            {
                if (msg == WM_RUN_QUEUED)
                {
                    while (self._queue.TryDequeue(out var a)) a();
                    return 0;
                }
                if (msg == WM_TRAY)
                {
                    var evt = (uint)(l & 0xFFFF);
                    if (evt is WM_LBUTTONUP or WM_RBUTTONUP or WM_CONTEXTMENU) self.ShowMenu();
                    return 0;
                }
                if (msg == self._taskbarCreated)
                {
                    self.AddIcon(); // Explorer restarted; put our icon back
                    return 0;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Tray error: {ex}");
        }
        return DefWindowProcW(hwnd, msg, w, l);
    }

    NOTIFYICONDATAW IconData()
    {
        var d = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
        };
        return d;
    }

    void AddIcon()
    {
        var d = IconData();
        Shell_NotifyIconW(NIM_ADD, &d);
        d.uVersion = 4;
        Shell_NotifyIconW(NIM_SETVERSION, &d);
    }

    void UpdateTooltip()
    {
        var m = _media.Snapshot();
        var title = (string?)m["title"];
        var song = string.IsNullOrEmpty(title) ? "Nothing playing" : $"{title} - {m["artist"]}";
        var v = _audio.VolumeInfo();
        var vol = (bool)v["muted"]! ? "muted" : $"{Math.Round((double)v["volume"]! * 100)}%";
        var text = $"MediaBridge\n{song}\n{_audio.DeviceName} ({vol})\nMods connected: {_server.ControlClientCount}";

        var d = IconData();
        var span = new Span<char>(d.szTip, 128);
        text.AsSpan(0, Math.Min(text.Length, 127)).CopyTo(span);
        span[Math.Min(text.Length, 127)] = '\0';
        Shell_NotifyIconW(NIM_MODIFY, &d);
    }

    // ---------------- menu ----------------

    readonly Dictionary<int, Action> _menuActions = [];

    nint Item(nint menu, string text, Action? onClick, bool isChecked = false, bool enabled = true)
    {
        int id = _menuActions.Count + 1;
        if (onClick != null) _menuActions[id] = onClick;
        uint flags = MF_STRING | (isChecked ? MF_CHECKED : 0) | (enabled ? 0 : MF_GRAYED);
        AppendMenuW(menu, flags, onClick != null ? id : 0, text.Replace("&", "&&"));
        return menu;
    }

    static void Separator(nint menu) => AppendMenuW(menu, MF_SEPARATOR, 0, null);

    void ShowMenu()
    {
        _menuActions.Clear();
        var menu = CreatePopupMenu();

        var m = _media.Snapshot();
        var title = (string?)m["title"];
        Item(menu, string.IsNullOrEmpty(title)
            ? "Nothing playing"
            : $"{((string?)m["status"] == "playing" ? "â–¶" : "âšâš")}  {Shorten(title, 40)} â€” {Shorten((string?)m["artist"] ?? "", 30)}",
            null, enabled: false);
        Item(menu, "Previous", () => _ = _media.SendAsync("previous"));
        Item(menu, "Play / Pause", () => _ = _media.SendAsync("play_pause"));
        Item(menu, "Next", () => _ = _media.SendAsync("next"));
        Separator(menu);

        // Device picker
        var devices = CreatePopupMenu();
        var list = _audio.ListDevices();
        AddDeviceGroup(devices, list, "output", "Outputs (records what apps play into it)");
        Separator(devices);
        AddDeviceGroup(devices, list, "input", "Inputs (records the device directly)");
        AppendMenuW(menu, MF_POPUP, devices, $"Listen to: {Shorten(_audio.DeviceName, 40)}".Replace("&", "&&"));

        // Volume
        var v = _audio.VolumeInfo();
        bool muted = (bool)v["muted"]!;
        var volume = CreatePopupMenu();
        foreach (var pct in new[] { 100, 75, 50, 25, 10 })
            Item(volume, $"{pct}%", () => _audio.SetVolume(pct / 100f));
        Separator(volume);
        Item(volume, muted ? "Unmute" : "Mute", () => _audio.SetMute(null));
        AppendMenuW(menu, MF_POPUP, volume, $"Volume: {Math.Round((double)v["volume"]! * 100)}%{(muted ? " (muted)" : "")}");
        Separator(menu);

        Item(menu, $"Connected mods: {_server.ControlClientCount}   (audio streams: {_server.AudioClientCount})", null, enabled: false);
        Item(menu, $"Radio stream listeners: {_radio.ListenerCount}", null, enabled: false);
        Item(menu, "Copy radio stream link", () => CopyToClipboard(_radio.Url));
        bool startup = IsStartupEnabled();
        Item(menu, "Start with Windows", () => SetStartup(!startup), isChecked: startup);
        Item(menu, "Open settings folder", () => System.Diagnostics.Process.Start("explorer.exe", Settings.DataDir));
        Separator(menu);
        Item(menu, "Exit", () => DestroyWindowAndQuit());

        POINT p;
        GetCursorPos(&p);
        SetForegroundWindow(_hwnd); // required, or the menu won't close when you click elsewhere
        int chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, p.x, p.y, _hwnd, 0);
        PostMessageW(_hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu); // also destroys the submenus

        if (chosen != 0 && _menuActions.TryGetValue(chosen, out var action)) action();
    }

    void AddDeviceGroup(nint menu, JsonArray list, string kind, string header)
    {
        Item(menu, header, null, enabled: false);
        foreach (var d in list)
        {
            if ((string?)d!["kind"] != kind) continue;
            var id = (string)d["id"]!;
            var name = (string)d["name"]!;
            if ((bool)d["isVirtualCable"]! && kind == "output") name += "   â† recommended";
            Item(menu, name, () =>
            {
                if (!_audio.TrySelect(id)) return;
                _settings.DeviceId = id;
                _settings.Save();
                UpdateTooltip();
            }, isChecked: (bool)d["selected"]!);
        }
    }

    void DestroyWindowAndQuit()
    {
        DestroyWindow(_hwnd);
        PostQuitMessage(0);
    }

    static void CopyToClipboard(string text)
    {
        // clip.exe is built into Windows and avoids a pile of clipboard API code.
        var psi = new System.Diagnostics.ProcessStartInfo("clip.exe") { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi);
        if (p == null) return;
        p.StandardInput.Write(text);
        p.StandardInput.Close();
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "â€¦";

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("MediaBridge") != null;
    }

    static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("MediaBridge", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("MediaBridge", false);
    }

    /// <summary>Draws a green circle with a music note, so we don't need an image file.</summary>
    static nint MakeIcon()
    {
        const int S = 32;
        var pixels = new uint[S * S];
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            float cx = x + 0.5f, cy = y + 0.5f;
            float circle = Coverage(MathF.Sqrt((cx - 16) * (cx - 16) + (cy - 16) * (cy - 16)) - 15.5f);
            float note = MathF.Max(MathF.Max(
                Coverage(Ellipse(cx, cy, 11, 23, 4.2f, 3.3f)),     // left note head
                Coverage(Ellipse(cx, cy, 21, 20.5f, 4.2f, 3.3f))), // right note head
                MathF.Max(MathF.Max(
                    Coverage(Segment(cx, cy, 14.6f, 22, 14.6f, 9) - 1.3f),  // left stem
                    Coverage(Segment(cx, cy, 24.6f, 19.5f, 24.6f, 6.5f) - 1.3f)), // right stem
                    Coverage(Segment(cx, cy, 14.6f, 9, 24.6f, 6.5f) - 1.8f))); // beam
            // green (30,185,110) blended with white note, premultiplied alpha
            float r = 30 + (255 - 30) * note, g = 185 + (255 - 185) * note, b = 110 + (255 - 110) * note;
            float a = circle;
            pixels[y * S + x] = ((uint)(a * 255) << 24) | ((uint)(r * a) << 16) | ((uint)(g * a) << 8) | (uint)(b * a);
        }

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = S, biHeight = -S, biPlanes = 1, biBitCount = 32,
        };
        void* bits;
        var color = CreateDIBSection(0, &bmi, 0, &bits, 0, 0);
        pixels.AsSpan().CopyTo(new Span<uint>(bits, S * S));
        var maskBits = new byte[S * S / 8];
        nint mask;
        fixed (byte* mb = maskBits) mask = CreateBitmap(S, S, 1, 1, mb);
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(&info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;

        static float Coverage(float distance) => Math.Clamp(0.5f - distance, 0f, 1f);
        static float Ellipse(float x, float y, float cx, float cy, float rx, float ry)
        {
            float dx = (x - cx) / rx, dy = (y - cy) / ry;
            return (MathF.Sqrt(dx * dx + dy * dy) - 1) * MathF.Min(rx, ry);
        }
        static float Segment(float x, float y, float ax, float ay, float bx, float by)
        {
            float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
            float t = Math.Clamp((px * dx + py * dy) / (dx * dx + dy * dy), 0, 1);
            float ex = px - dx * t, ey = py - dy * t;
            return MathF.Sqrt(ex * ex + ey * ey);
        }
    }
}
