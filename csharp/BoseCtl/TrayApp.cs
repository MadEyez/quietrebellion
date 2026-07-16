/*
 * TrayApp.cs – Windows system-tray UI for Bose QC Ultra control.
 *
 * Menu layout:
 *   [ Device — 90%  ✓ ]
 *   ────────────────────
 *   ✓ Quiet   Aware   Immersion   Cinema   [custom slots]
 *   ────────────────────
 *   CNC Level: 5  ▶ (0=max ANC … 10=max aware)
 *   Spatial Audio  ▶ (Off / Room / Head)
 *   Wind Block: Off   [toggle]
 *   ────────────────────
 *   Equalizer  ▶
 *     Bass:  0 dB  ▶ (-10..+10)
 *     Mid:   0 dB  ▶
 *     Treble: 0 dB ▶
 *   Sidetone  ▶ (Off / Low / Medium / High)
 *   Multipoint: On  [toggle]
 *   ────────────────────
 *   Device Info  ▶
 *     Bose QC Ultra Headphones
 *     Firmware: 8.2.20+...
 *     Rename Device…
 *   ────────────────────
 *   Reconnect
 *   Exit
 */

using System.Drawing;
using System.Windows.Forms;

namespace BoseCtl;

public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _pollTimer;

    private BoseConnection?   _conn;
    private DiscoveredDevice? _device;

    // ── Cached device state ───────────────────────────────────────────────────
    private string  _deviceName = "Bose QC Ultra";
    private string  _firmware   = "";
    private int     _battery    = -1;
    private bool?   _charging   = null;
    private int     _modeIndex  = -1;
    private int     _cncLevel   = 0;
    private bool    _autoCnc    = false;
    private bool    _ancOn      = false;
    private bool    _windBlock  = false;
    private SpatialMode _spatial   = SpatialMode.Off;
    private int     _sidetone   = 0;   // 0=off 1=high 2=medium 3=low
    private bool    _multipoint = false;
    private List<EqBand> _eq   = new();
    private Dictionary<int, string> _modeNames = new();
    private IReadOnlySet<int> _favorites = new HashSet<int>();
    private int _favoritesTotalModes = 11;
    private bool _autoPlayPause = false;
    private bool _autoAnswer    = false;

    // ── Multipoint switch state ───────────────────────────────────────────────
    private List<(string mac, string label)> _pairedDevices = new();
    private string? _activeMac = null;   // MAC of currently active BT source

    private bool _busy = false;
    private int  _lowBatteryWarnedAt = -1;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => RebuildMenu(menu);

        _tray = new NotifyIcon
        {
            Icon             = BmapIcons.Searching(),
            Text             = "bosectl — searching…",
            Visible          = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) menu.Show(Cursor.Position);
        };

        _pollTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _pollTimer.Tick += async (s, e) => await PollAsync();
        _pollTimer.Start();

        Application.Idle += OnFirstIdle;
    }

    // ── Connection lifecycle ──────────────────────────────────────────────────

    private async void OnFirstIdle(object? s, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            SetTray(BmapIcons.Searching(), "bosectl — connecting…");
            if (_conn is not null) { await _conn.DisposeAsync(); _conn = null; }

            _device = await DeviceDiscovery.FindAsync();
            if (_device is null)
            {
                SetTray(BmapIcons.Disconnected(), "bosectl — device not found");
                return;
            }

            _conn       = await BoseConnection.ConnectAsync(_device);
            _deviceName = _device.Name;

            // ── Fetch once-on-connect data ────────────────────────────────────
            try { _firmware   = await _conn.FirmwareAsync();          } catch { }
            try { _modeNames  = await _conn.GetAllModeNamesAsync();   } catch { _modeNames = new(QcUltra2.ModeNames); }
            try { _eq         = await _conn.EqAsync();                } catch { }
            try { _sidetone   = await _conn.SidetoneAsync();          } catch { }
            try { _multipoint = await _conn.MultipointAsync();        } catch { }
            try { (_favorites, _favoritesTotalModes) = await _conn.FavoritesAsync(); } catch { }
            try { _autoPlayPause  = await _conn.AutoPlayPauseAsync();   } catch { }
            try { _autoAnswer     = await _conn.AutoAnswerAsync();      } catch { }
            try { await RefreshMultipointDevicesAsync();               } catch { }

            await DoPollAsync();
        }
        catch (Exception ex)
        {
            if (_conn is not null) { await _conn.DisposeAsync(); _conn = null; }
            SetTray(BmapIcons.Disconnected(),
                $"bosectl — {ex.Message[..Math.Min(60, ex.Message.Length)]}");
        }
        finally { _busy = false; }
    }

    private async Task PollAsync()
    {
        if (_busy) return;
        if (_conn is null) { await ConnectAsync(); return; }
        _busy = true;
        try   { await DoPollAsync(); }
        catch
        {
            if (_conn is not null) { await _conn.DisposeAsync(); _conn = null; }
            SetTray(BmapIcons.Disconnected(), "bosectl — disconnected (retrying…)");
        }
        finally { _busy = false; }
    }

    private async Task DoPollAsync()
    {
        _battery    = await _conn!.BatteryAsync();
        _charging   = await _conn.ChargingStateAsync();
        _modeIndex  = await _conn.ModeIndexAsync();
        var audio   = await _conn.AudioSettingsAsync();
        _ancOn      = audio.AncToggle;
        _autoCnc    = audio.AutoCnc;
        _cncLevel   = audio.CncLevel;
        _windBlock  = audio.WindBlock;
        _spatial    = audio.Spatial;

        string chargingIcon = _charging == true ? " ⚡" : "";
        SetTray(BmapIcons.Connected(_battery),
            $"bosectl — {_deviceName} | {_battery}%{chargingIcon} | {ModeLabel(_modeIndex)}");
        NotifyLowBattery();
    }

    // ── Menu ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Refresh paired-device list and active-source MAC from the headphone.
    /// Called once on connect (and after each switch to update ✓ marker).
    /// </summary>
    private async Task RefreshMultipointDevicesAsync()
    {
        if (_conn is null) return;

        // Active source → gives us the MAC of the currently playing device
        try
        {
            var (_, mac) = await _conn.GetSourceAsync();
            _activeMac = mac;
        }
        catch { _activeMac = null; }

        // Paired device list → gives us all MACs the headphone knows
        try
        {
            var macs = await _conn.GetPairedDeviceMacsAsync();
            _pairedDevices = new List<(string mac, string label)>();
            foreach (var mac in macs)
            {
                string label = await ResolveBtNameAsync(mac) ?? ShortenMac(mac);
                _pairedDevices.Add((mac, label));
            }
        }
        catch { }
    }

    /// <summary>
    /// Resolve a Bluetooth MAC to a human-readable name using two strategies:
    ///  1. Compare against this PC's own BT adapter address → "This PC"
    ///  2. Look up via Windows BluetoothDevice cache (finds devices also paired
    ///     with this Windows PC, e.g. the user's phone)
    /// Returns null when no clean name is found (caller shows shortened MAC).
    /// </summary>
    private static async Task<string?> ResolveBtNameAsync(string mac)
    {
        try
        {
            ulong addr = MacToUlong(mac);

            // Strategy 1: check ALL local BT adapters (covers BTD 700 + built-in)
            string selector = Windows.Devices.Bluetooth.BluetoothAdapter
                .GetDeviceSelector();
            var adapterInfos = await Windows.Devices.Enumeration.DeviceInformation
                .FindAllAsync(selector);
            foreach (var info in adapterInfos)
            {
                try
                {
                    var a = await Windows.Devices.Bluetooth.BluetoothAdapter
                        .FromIdAsync(info.Id);
                    if (a?.BluetoothAddress == addr) return "This PC";
                }
                catch { }
            }

            // Strategy 2: Windows BT paired-device cache
            var dev = await Windows.Devices.Bluetooth.BluetoothDevice
                .FromBluetoothAddressAsync(addr);
            string? name = dev?.Name;
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (name.Contains(':'))              return null;
            if (name.StartsWith("Bluetooth", StringComparison.OrdinalIgnoreCase))
                return null;

            return name;
        }
        catch { return null; }
    }

    private static ulong MacToUlong(string mac) =>
        mac.Split(':')
           .Select(b => Convert.ToByte(b, 16))
           .Aggregate(0UL, (acc, b) => (acc << 8) | b);

    private static string ShortenMac(string mac)
    {
        var parts = mac.Split(':');
        return parts.Length >= 3
            ? "…" + string.Join(":", parts.TakeLast(3))
            : mac;
    }

    private void RebuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        // Header
        string chargingIcon = _charging == true ? " ⚡" : "";
        menu.Items.Add(new ToolStripMenuItem(
            _conn is not null
                ? $"{_deviceName}  —  {(_battery >= 0 ? $"{_battery}%{chargingIcon}" : "?")}  ✓"
                : "Not connected")
            { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        if (_conn is not null)
        {
            // ── Modes ─────────────────────────────────────────────────────────
            foreach (var (idx, name) in _modeNames.OrderBy(kv => kv.Key))
            {
                bool cur  = idx == _modeIndex;
                bool isFav = _favorites.Contains(idx);
                string favStar = isFav ? "★ " : "     ";
                var  item = new ToolStripMenuItem((cur ? "✓  " : "     ") + favStar + Capitalize(name));
                int  ci   = idx;
                item.Click += async (_, _) => await SwitchModeAsync(ci);
                menu.Items.Add(item);
            }
            menu.Items.Add(FavoritesMenu());
            menu.Items.Add(new ToolStripSeparator());

            // ── Noise control ─────────────────────────────────────────────────
            menu.Items.Add(CncMenu());
            menu.Items.Add(SpatialMenu());
            menu.Items.Add(ToggleItem(
                $"Wind Block:  {(_windBlock ? "On" : "Off")}",
                async () => { await _conn.SetWindBlockAsync(!_windBlock); _windBlock = !_windBlock; }));
            menu.Items.Add(new ToolStripSeparator());

            // ── Audio ─────────────────────────────────────────────────────────
            menu.Items.Add(EqMenu());
            menu.Items.Add(SidetoneMenu());
            menu.Items.Add(ToggleItem(
                $"Multipoint:  {(_multipoint ? "On" : "Off")}",
                async () => { await _conn.SetMultipointAsync(!_multipoint); _multipoint = !_multipoint; }));

            // Show "Switch Device" only when multipoint is active and we have devices
            if (_multipoint && _pairedDevices.Count > 0)
                menu.Items.Add(SwitchDeviceMenu());
            menu.Items.Add(new ToolStripSeparator());

            // ── Settings ──────────────────────────────────────────────────────
            menu.Items.Add(SettingsMenu());
            menu.Items.Add(new ToolStripSeparator());

            // ── Device info ───────────────────────────────────────────────────
            menu.Items.Add(DeviceInfoMenu());
            menu.Items.Add(new ToolStripSeparator());
        }

        var reconnect = new ToolStripMenuItem("Reconnect");
        reconnect.Click += async (_, _) => await ConnectAsync();
        menu.Items.Add(reconnect);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => { _tray.Visible = false; Application.Exit(); };
        menu.Items.Add(exit);
    }

    // ── Submenu builders ─────────────────────────────────────────────────────

    private ToolStripMenuItem CncMenu()
    {
        string label = _autoCnc ? $"CNC Level:  Auto" : $"CNC Level:  {_cncLevel}";
        var parent = new ToolStripMenuItem(label);

        // Auto-CNC toggle at top
        bool autoCur = _autoCnc;
        var autoItem = new ToolStripMenuItem((autoCur ? "✓  " : "     ") + "Auto (device-managed)");
        autoItem.Click += async (_, _) =>
        {
            await _conn!.SetAutoCncAsync(!_autoCnc);
            _autoCnc = !_autoCnc;
        };
        parent.DropDownItems.Add(autoItem);
        parent.DropDownItems.Add(new ToolStripSeparator());

        for (int lvl = 0; lvl <= 10; lvl++)
        {
            string suffix = lvl == 0 ? "  (max ANC)" : lvl == 10 ? "  (max aware)" : "";
            bool cur = !_autoCnc && lvl == _cncLevel;
            var  item = new ToolStripMenuItem((cur ? "✓  " : "     ") + lvl + suffix);
            int  cl   = lvl;
            item.Click += async (_, _) => { await _conn!.SetCncLevelAsync(cl); _cncLevel = cl; _autoCnc = false; };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    private ToolStripMenuItem FavoritesMenu()
    {
        var parent = new ToolStripMenuItem("Favourites ★");
        foreach (var (idx, name) in _modeNames.OrderBy(kv => kv.Key))
        {
            bool isFav = _favorites.Contains(idx);
            var  item  = new ToolStripMenuItem((isFav ? "★  " : "☆  ") + Capitalize(name));
            int  ci    = idx;
            item.Click += async (_, _) =>
            {
                var newFavs = new HashSet<int>(_favorites);
                if (!newFavs.Add(ci)) newFavs.Remove(ci);
                await _conn!.SetFavoritesAsync(newFavs, _favoritesTotalModes);
                _favorites = newFavs;
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    private ToolStripMenuItem SpatialMenu()
    {
        var parent = new ToolStripMenuItem($"Spatial Audio:  {_spatial}");
        foreach (SpatialMode mode in Enum.GetValues<SpatialMode>())
        {
            bool cur  = mode == _spatial;
            var  item = new ToolStripMenuItem((cur ? "✓  " : "     ") + mode);
            var  cm   = mode;
            item.Click += async (_, _) =>
            {
                await _conn!.SetSpatialAsync(cm);
                _spatial = cm;
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    private ToolStripMenuItem EqMenu()
    {
        var parent = new ToolStripMenuItem("Equalizer");
        if (_eq.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("(no data)") { Enabled = false });
            return parent;
        }
        foreach (var band in _eq.OrderBy(b => b.BandId))
        {
            var bandMenu = new ToolStripMenuItem(
                $"  {band.Name,-7}  {band.Current:+0;-0;0} dB");
            var capturedBand = band;
            // Values from max down to min (top = highest)
            for (int v = band.MaxVal; v >= band.MinVal; v--)
            {
                bool cur  = v == band.Current;
                var  item = new ToolStripMenuItem(
                    (cur ? "✓  " : "     ") + $"{v:+0;-0;0} dB");
                int  cv   = v;
                item.Click += async (_, _) =>
                {
                    await _conn!.SetEqBandAsync(capturedBand.BandId, cv);
                    // Update cached EQ value
                    int i = _eq.FindIndex(b => b.BandId == capturedBand.BandId);
                    if (i >= 0) _eq[i] = _eq[i] with { Current = cv };
                };
                bandMenu.DropDownItems.Add(item);
            }
            parent.DropDownItems.Add(bandMenu);
        }
        return parent;
    }

    private ToolStripMenuItem SidetoneMenu()
    {
        string curName = QcUltra2.SidetoneNames.TryGetValue(_sidetone, out var sn) ? sn : "off";
        var parent = new ToolStripMenuItem($"Sidetone:  {Capitalize(curName)}");
        foreach (var (lvl, name) in QcUltra2.SidetoneNames.OrderBy(kv => kv.Key))
        {
            bool cur  = lvl == _sidetone;
            var  item = new ToolStripMenuItem((cur ? "✓  " : "     ") + Capitalize(name));
            int  cl   = lvl;
            item.Click += async (_, _) =>
            {
                await _conn!.SetSidetoneAsync(cl);
                _sidetone = cl;
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    private ToolStripMenuItem SwitchDeviceMenu()
    {
        string activeLabel = _pairedDevices
            .FirstOrDefault(d => d.mac.Equals(_activeMac, StringComparison.OrdinalIgnoreCase)).label
            ?? "unknown";
        var parent = new ToolStripMenuItem($"Switch Device:  {activeLabel}");

        foreach (var (mac, label) in _pairedDevices)
        {
            bool isCurrent = mac.Equals(_activeMac, StringComparison.OrdinalIgnoreCase);
            var item = new ToolStripMenuItem((isCurrent ? "✓  " : "     ") + label);
            string capturedMac = mac;
            item.Click += async (_, _) =>
            {
                try
                {
                    SetTray(BmapIcons.Connected(_battery),
                        $"bosectl — switching to {label}…");
                    await _conn!.SwitchToDeviceAsync(capturedMac);
                    _activeMac = capturedMac;
                    SetTray(BmapIcons.Connected(_battery),
                        $"bosectl — {_deviceName} | {_battery}%");
                }
                catch (Exception ex)
                {
                    ShowBalloon("Device switch failed", ex.Message, ToolTipIcon.Warning);
                }
            };
            parent.DropDownItems.Add(item);
        }

        parent.DropDownItems.Add(new ToolStripSeparator());
        var refresh = new ToolStripMenuItem("  Refresh device list");
        refresh.Click += async (_, _) =>
        {
            try { await RefreshMultipointDevicesAsync(); }
            catch { /* ignore */ }
        };
        parent.DropDownItems.Add(refresh);

        return parent;
    }

    private ToolStripMenuItem SettingsMenu()
    {
        var parent = new ToolStripMenuItem("Settings");


        // Auto Play/Pause
        parent.DropDownItems.Add(ToggleItem(
            $"Auto Play/Pause:  {(_autoPlayPause ? "On" : "Off")}",
            async () => { await _conn!.SetAutoPlayPauseAsync(!_autoPlayPause); _autoPlayPause = !_autoPlayPause; }));

        // Auto Answer
        parent.DropDownItems.Add(ToggleItem(
            $"Auto Answer:  {(_autoAnswer ? "On" : "Off")}",
            async () => { await _conn!.SetAutoAnswerAsync(!_autoAnswer); _autoAnswer = !_autoAnswer; }));

        return parent;
    }

    private ToolStripMenuItem DeviceInfoMenu()
    {
        var parent = new ToolStripMenuItem("Device Info");
        parent.DropDownItems.Add(
            new ToolStripMenuItem($"  {_deviceName}") { Enabled = false });
        parent.DropDownItems.Add(
            new ToolStripMenuItem($"  Firmware: {_firmware}") { Enabled = false });
        parent.DropDownItems.Add(new ToolStripSeparator());
        var rename = new ToolStripMenuItem("  Rename Device…");
        rename.Click += async (_, _) =>
        {
            string? newName = ShowInputDialog(
                "Rename Device", "Enter new device name:", _deviceName);
            if (newName is not null && newName.Length > 0 && newName != _deviceName)
            {
                await _conn!.SetDeviceNameAsync(newName);
                _deviceName = newName;
                SetTray(BmapIcons.Connected(_battery),
                    $"bosectl — {_deviceName} | {_battery}%");
            }
        };
        parent.DropDownItems.Add(rename);
        parent.DropDownItems.Add(new ToolStripSeparator());

        // Power Off
        var powerOff = new ToolStripMenuItem("  Power Off");
        powerOff.Click += async (_, _) =>
        {
            if (MessageBox.Show($"Power off {_deviceName}?", "Power Off",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            {
                await _conn!.PowerOffAsync();
                if (_conn is not null) { await _conn.DisposeAsync(); _conn = null; }
                SetTray(BmapIcons.Disconnected(), "bosectl — powered off");
            }
        };
        parent.DropDownItems.Add(powerOff);

        // Pairing Mode
        var pairing = new ToolStripMenuItem("  Enter Pairing Mode");
        pairing.Click += async (_, _) =>
        {
            if (MessageBox.Show($"Put {_deviceName} into Bluetooth pairing mode?\nThe headphones will disconnect.",
                    "Pairing Mode", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            {
                await _conn!.EnterPairingModeAsync();
                if (_conn is not null) { await _conn.DisposeAsync(); _conn = null; }
                SetTray(BmapIcons.Disconnected(), "bosectl — pairing mode");
            }
        };
        parent.DropDownItems.Add(pairing);
        return parent;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ToolStripMenuItem ToggleItem(string label, Func<Task> action)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += async (_, _) => await action();
        return item;
    }

    private async Task SwitchModeAsync(int idx)
    {
        if (_conn is null) return;
        string name = ModeLabel(idx);
        SetTray(BmapIcons.Connected(_battery), $"bosectl — switching to {name}…");
        try
        {
            await _conn.SetModeByIndexAsync(idx);
            _modeIndex = idx;
            SetTray(BmapIcons.Connected(_battery),
                $"bosectl — {_deviceName} | {_battery}% | {name}");
        }
        catch (Exception ex)
        {
            ShowBalloon("Mode switch failed", ex.Message, ToolTipIcon.Warning);
            SetTray(BmapIcons.Connected(_battery),
                $"bosectl — {_deviceName} | {_battery}%");
        }
    }

    private string ModeLabel(int idx) => QcUltra2.ModeDisplayName(idx, _modeNames);

    private void SetTray(Icon icon, string text)
    {
        _tray.Icon = icon;
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText  = text;
        _tray.BalloonTipIcon  = icon;
        _tray.ShowBalloonTip(4000);
    }

    private void NotifyLowBattery()
    {
        if (_battery <= 20 && _battery != _lowBatteryWarnedAt)
        {
            _lowBatteryWarnedAt = _battery;
            ShowBalloon("Low Battery",
                $"{_deviceName}: {_battery}% remaining", ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// Simple one-line input dialog (replaces VB6 InputBox with no extra dependency).
    /// </summary>
    private static string? ShowInputDialog(string title, string prompt, string defaultValue)
    {
        using var form = new Form
        {
            Text            = title,
            ClientSize      = new Size(380, 110),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition   = FormStartPosition.CenterScreen,
            MaximizeBox     = false, MinimizeBox = false,
        };
        var lbl    = new Label  { Text = prompt,       Left = 10, Top = 12, Width = 360 };
        var txt    = new TextBox { Text = defaultValue, Left = 10, Top = 34, Width = 360 };
        var ok     = new Button { Text = "OK",     Left = 290, Top = 68, Width = 80,
                                   DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 200, Top = 68, Width = 80,
                                   DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        txt.SelectAll();
        return form.ShowDialog() == DialogResult.OK ? txt.Text.Trim() : null;
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tray.Visible = false; _tray.Dispose(); _pollTimer.Dispose(); }
        base.Dispose(disposing);
    }
}

