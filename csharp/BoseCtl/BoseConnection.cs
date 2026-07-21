/*
 * BoseConnection.cs – High-level BMAP operations for QC Ultra (wolverine).
 *
 * Port of bosectl/python/pybmap/connection.py (read + write paths).
 *
 * Each method maps directly to one or two BMAP round-trips. Addresses and
 * parsers are imported from QcUltra2 so this class stays protocol-agnostic.
 * Adding a new device requires only a new feature-map class and changing
 * the static factory to select the right one.
 */

namespace BoseCtl;

public sealed class BoseConnection : IAsyncDisposable
{
    private readonly IAsyncDisposable _transport;
    private readonly WinsockRfcommTransport _t;
    private readonly bool            _debug;

    public BoseConnection(WinsockRfcommTransport transport, bool debug = false)
    {
        _t         = transport;
        _transport = transport;
        _debug     = debug;
    }

    // ── Factories ─────────────────────────────────────────────────────────────

    public static async Task<BoseConnection> ConnectAsync(
        DiscoveredDevice device, bool debug = false)
    {
        // Use raw Winsock AF_BTH to connect directly to RFCOMM channel 2,
        // bypassing SDP. Pass BluetoothAddress (ulong) directly to avoid
        // any MAC string parsing.
        var transport = await WinsockRfcommTransport.ConnectAsync(
            device.BluetoothAddress, QcUltra2.RfcommChannel, debug);
        return new BoseConnection(transport, debug);
    }

    // ── Write Operations ──────────────────────────────────────────────────────

    // ...existing SetModeAsync, SetCncLevelAsync, SetAncAsync, SetSpatialAsync...

    /// <summary>
    /// Set wind-block on/off.
    /// bmap: GET [31.10] then SETGET [31.10].
    /// </summary>
    public async Task SetWindBlockAsync(bool enabled)
    {
        var s = await AudioSettingsAsync();
        await WriteAudioSettingsAsync(s with { WindBlock = enabled });
    }

    /// <summary>
    /// Set sidetone level (0=off, 1=high, 2=medium, 3=low).
    /// bmap: SETGET [1.11] payload=[1, level]
    /// </summary>
    public async Task SetSidetoneAsync(int level)
    {
        var (fb, fn) = QcUltra2.Sidetone;
        var pkt = BmapProtocol.Build(fb, fn, Op.SetGet, QcUltra2.BuildSidetone(level));
        var raw = await _t.SendRecvAsync(pkt);
        ThrowIfError(FindResponse(raw, fb, fn));
    }

    /// <summary>
    /// Toggle multipoint on/off.
    /// bmap: SETGET [1.10] payload=[1] on / [0] off
    /// </summary>
    public async Task SetMultipointAsync(bool enabled)
    {
        var (fb, fn) = QcUltra2.Multipoint;
        var pkt = BmapProtocol.Build(fb, fn, Op.SetGet, QcUltra2.BuildToggle(enabled));
        var raw = await _t.SendRecvAsync(pkt);
        ThrowIfError(FindResponse(raw, fb, fn));
    }

    /// <summary>
    /// Set one EQ band.
    /// bmap: SETGET [1.7] payload=[value_signed_byte, band_id]
    /// band_id: 0=Bass, 1=Mid, 2=Treble   value: -10..+10
    /// </summary>
    public async Task SetEqBandAsync(int bandId, int value)
    {
        if (value < -10 || value > 10)
            throw new ArgumentOutOfRangeException(nameof(value), "EQ value must be -10..+10");
        var (fb, fn) = QcUltra2.Eq;
        var pkt = BmapProtocol.Build(fb, fn, Op.SetGet, QcUltra2.BuildEqBand(value, bandId));
        var raw = await _t.SendRecvAsync(pkt);
        ThrowIfError(FindResponse(raw, fb, fn));
    }

    /// <summary>
    /// Change device Bluetooth display name.
    /// bmap: SETGET [1.2] payload=UTF-8 name bytes
    /// </summary>
    public async Task SetDeviceNameAsync(string name)
    {
        var (fb, fn) = QcUltra2.ProductName;
        var pkt = BmapProtocol.Build(fb, fn, Op.SetGet, QcUltra2.BuildDeviceName(name));
        var raw = await _t.SendRecvAsync(pkt);
        ThrowIfError(FindResponse(raw, fb, fn));
    }

    // ── Additional Read Operations ────────────────────────────────────────────

    /// <summary>
    /// Sidetone level: 0=off, 1=high, 2=medium, 3=low.
    /// bmap: GET [1.11] → [persist, level]
    /// </summary>
    public async Task<int> SidetoneAsync()
    {
        var (fb, fn) = QcUltra2.Sidetone;
        var resp = await GetAsync(fb, fn);
        return QcUltra2.ParseSidetone(resp.Payload);
    }

    /// <summary>
    /// Multipoint connection enabled.
    /// bmap: GET [1.10] → payload[0] bit-1
    /// </summary>
    public async Task<bool> MultipointAsync()
    {
        var (fb, fn) = QcUltra2.Multipoint;
        var resp = await GetAsync(fb, fn);
        return QcUltra2.ParseMultipoint(resp.Payload);
    }

    /// <summary>
    /// Battery level 0–100 %.
    /// bmap: GET [2.2] → payload[0]
    /// </summary>
    public async Task<int> BatteryAsync()
    {
        var (fb, fn) = QcUltra2.Battery;
        var resp = await GetAsync(fb, fn);
        return QcUltra2.ParseBattery(resp.Payload);
    }

    /// <summary>
    /// Firmware version string.
    /// bmap: GET [0.5] → ASCII string
    /// </summary>
    public async Task<string> FirmwareAsync()
    {
        var (fb, fn) = QcUltra2.Firmware;
        var resp = await GetAsync(fb, fn);
        return QcUltra2.ParseFirmware(resp.Payload);
    }

    /// <summary>
    /// Device Bluetooth name.
    /// bmap: GET [1.2] → [flag, ...utf8_name]
    /// </summary>
    public async Task<string> DeviceNameAsync()
    {
        var (fb, fn) = QcUltra2.ProductName;
        var resp = await GetAsync(fb, fn);
        return QcUltra2.ParseProductName(resp.Payload);
    }

    /// <summary>
    /// Current noise-control mode index.
    /// bmap: GET [31.3] → [mode_index]
    /// </summary>
    public async Task<int> ModeIndexAsync()
    {
        var (fb, fn) = QcUltra2.CurrentMode;
        var resp = await GetAsync(fb, fn);
        return resp.Payload.Length > 0 ? resp.Payload[0] : -1;
    }

    /// <summary>
    /// Current mode as a human-readable name ("quiet", "aware", "immersion", "cinema",
    /// or "custom(N)" for user-defined profiles).
    /// </summary>
    public async Task<string> ModeNameAsync()
    {
        int idx = await ModeIndexAsync();
        return QcUltra2.ModeNames.TryGetValue(idx, out var name) ? name : $"custom({idx})";
    }

    /// <summary>
    /// Current audio settings (CNC level, ANC toggle, spatial, wind block).
    /// bmap: GET [31.10] → 5-byte payload
    /// </summary>
    public async Task<AudioSettings> AudioSettingsAsync()
    {
        var (fb, fn) = QcUltra2.AudioSettings;
        var resp = await GetAsync(fb, fn);
        return QcUltra2.ParseAudioSettings(resp.Payload);
    }

    /// <summary>
    /// EQ bands (Bass, Mid, Treble with min/max/current).
    /// bmap: GET [1.7] → 4-byte groups per band
    /// </summary>
    public async Task<List<EqBand>> EqAsync()
    {
        var (fb, fn) = QcUltra2.Eq;
        var resp = await GetAsync(fb, fn);
        return QcUltra2.ParseEq(resp.Payload);
    }

    /// <summary>
    /// All mode configurations. Returns dict of index → display name.
    /// Preset slots 0-3 come from QcUltra2.ModeNames; custom slots (4-10)
    /// are populated by draining STATUS [31.6] packets from GetAllModes START.
    /// bmap: START [31.1] → drain multiple STATUS [31.6] packets
    /// </summary>
    public async Task<Dictionary<int, string>> GetAllModeNamesAsync()
    {
        var (fb, fn) = QcUltra2.GetAllModes;
        var pkt       = BmapProtocol.Build(fb, fn, Op.Start);
        var raw       = await _t.SendRecvAsync(pkt, drain: true);
        var responses = BmapProtocol.ParseAll(raw);

        // Seed with known preset names
        var names = new Dictionary<int, string>(
            QcUltra2.ModeNames.ToDictionary(kv => kv.Key, kv => kv.Value));

        var (cfgFb, cfgFn) = QcUltra2.ModeConfigStatus;
        foreach (var r in responses)
        {
            if (r.FBlock == cfgFb && r.Func == cfgFn && r.Operator == Op.Status)
            {
                var parsed = QcUltra2.ParseModeConfigBasic(r.Payload);
                if (parsed.HasValue)
                    names[parsed.Value.idx] = parsed.Value.name;
            }
        }
        if (_debug)
            Console.WriteLine($"[bose] modes: " +
                string.Join(", ", names.Select(kv => $"{kv.Key}={kv.Value}")));
        return names;
    }

    /// <summary>
    /// Switch to any mode by raw index (preset 0-3 or custom 4-10).
    /// bmap: START [31.3] payload=[mode_index, announce_flag]
    /// </summary>
    public async Task SetModeByIndexAsync(int index, bool announce = false)
    {
        var (fb, fn)  = QcUltra2.CurrentMode;
        byte[] payload = new byte[] { (byte)index, (byte)(announce ? 1 : 0) };
        var pkt  = BmapProtocol.Build(fb, fn, Op.Start, payload);
        var raw  = await _t.SendRecvAsync(pkt);
        var resp = FindResponse(raw, fb, fn);
        ThrowIfError(resp);
        if (_debug && resp is not null)
            Console.WriteLine($"[bose] set_mode_by_index({index}): {resp.Format()}");
    }

    /// <summary>
    /// Active audio source (type + Bluetooth MAC of sending device).
    /// bmap: GET [5.1] → [supported_hi, supported_lo, type, ...mac]
    /// </summary>
    public async Task<(string type, string? mac)> GetSourceAsync()
    {
        var (fb, fn) = QcUltra2.Source;
        var resp = await GetAsync(fb, fn);
        return QcUltra2.ParseSource(resp.Payload);
    }

    /// <summary>
    /// Bluetooth devices currently paired with the headphone.
    /// bmap: GET [4.4] → [flags, mac1(6), mac2(6), ...]
    /// </summary>
    public async Task<List<string>> GetPairedDeviceMacsAsync()
    {
        var (fb, fn) = QcUltra2.PairedDevices;
        var resp = await GetAsync(fb, fn);
        return QcUltra2.ParsePairedDevices(resp.Payload);
    }

    /// <summary>
    /// Paired device MACs mapped to their display names from headphone memory.
    /// For each MAC, queries [4.5] DeviceInfo. Falls back to null (caller uses MAC).
    /// Port of BoseConnection.kt pairedDevicesWithNames().
    /// </summary>
    public async Task<Dictionary<string, string?>> PairedDevicesWithNamesAsync()
    {
        var macs   = await GetPairedDeviceMacsAsync();
        var (fb, fn) = QcUltra2.DeviceInfo;
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var mac in macs)
        {
            string? name = null;
            try
            {
                byte[] macBytes = mac.Split(':').Select(b => Convert.ToByte(b, 16)).ToArray();
                var pkt  = BmapProtocol.Build(fb, fn, Op.Get, macBytes);
                var raw  = await _t.SendRecvAsync(pkt);
                var resp = FindResponse(raw, fb, fn);
                if (resp?.Operator == Op.Status)
                    name = QcUltra2.ParseDeviceInfo(resp.Payload);
            }
            catch { }
            result[mac] = name;
        }
        return result;
    }

    /// <summary>
    /// Route audio to a different paired Bluetooth device.
    /// bmap: START [4.12] payload=[0x82, mac0…mac5]
    /// The target MAC is the BT address of the device to receive audio
    /// (e.g. your phone's MAC when you want to switch from PC to phone).
    /// </summary>
    public async Task SwitchToDeviceAsync(string mac)
    {
        var (fb, fn) = QcUltra2.Routing;
        var payload = QcUltra2.BuildRouting(mac);
        var pkt = BmapProtocol.Build(fb, fn, Op.Start, payload);
        var raw = await _t.SendRecvAsync(pkt);
        ThrowIfError(FindResponse(raw, fb, fn));
        if (_debug) Console.WriteLine($"[bose] switched audio to {mac}");
    }

    /// <summary>
    /// Switch to a preset mode by name ("quiet", "aware", "immersion", "cinema").
    /// bmap: START [31.3] payload=[mode_index, announce_flag]
    /// No auth required for preset modes.
    /// </summary>
    public async Task SetModeAsync(string name, bool announce = false)
    {
        if (!QcUltra2.ModeIndexByName.TryGetValue(name, out int idx))
            throw new ArgumentException(
                $"Unknown mode '{name}'. Valid: {string.Join(", ", QcUltra2.ModeIndexByName.Keys)}");

        var (fb, fn) = QcUltra2.CurrentMode;
        byte[] payload = new byte[] { (byte)idx, (byte)(announce ? 1 : 0) };
        var pkt = BmapProtocol.Build(fb, fn, Op.Start, payload);
        var raw = await _t.SendRecvAsync(pkt);
        var resp = FindResponse(raw, fb, fn);
        ThrowIfError(resp);
        if (_debug && resp is not null)
            Console.WriteLine($"[bose] set_mode response: {resp.Format()}");
        // bmap: expected response op=RESULT (6)
        if (resp?.Operator != Op.Result)
            Console.WriteLine($"[warn] unexpected response to SetMode: {resp?.Format()}");
    }

    /// <summary>
    /// Set noise cancellation level 0–10.
    /// Reads current audio settings and writes back with the overridden CNC level.
    /// Forces AutoCnc=false so the explicit level is not ignored by the firmware.
    /// bmap: GET [31.10] then SETGET [31.10] with new payload.
    /// </summary>
    public async Task SetCncLevelAsync(int level)
    {
        if (level is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(level), "CNC level must be 0–10");
        var s = await AudioSettingsAsync();
        // AutoCnc=true → firmware ignores explicit level; must clear it first.
        await WriteAudioSettingsAsync(s with { CncLevel = level, AutoCnc = false });
    }

    /// <summary>
    /// Toggle ANC on or off.
    /// bmap: GET [31.10] then SETGET [31.10].
    /// </summary>
    public async Task SetAncAsync(bool enabled)
    {
        var s = await AudioSettingsAsync();
        await WriteAudioSettingsAsync(s with { AncToggle = enabled });
    }

    /// <summary>
    /// Toggle Auto-CNC (device-managed noise control level) on or off.
    /// bmap: GET [31.10] then SETGET [31.10].
    /// </summary>
    public async Task SetAutoCncAsync(bool enabled)
    {
        var s = await AudioSettingsAsync();
        await WriteAudioSettingsAsync(s with { AutoCnc = enabled });
    }

    /// <summary>
    /// Charging state: true=charging, false=not charging, null=unknown.
    /// bmap: GET [2.3] → [0=not charging, 1=charging]
    /// </summary>
    public async Task<bool?> ChargingStateAsync()
    {
        var (fb, fn) = QcUltra2.ChargingState;
        try
        {
            var resp = await GetAsync(fb, fn);
            return QcUltra2.ParseChargingState(resp.Payload);
        }
        catch { return null; }
    }

    /// <summary>
    /// Favourite mode indices and total mode count.
    /// bmap: GET [31.8] → [totalModes, 0x00, maskByte]
    /// </summary>
    public async Task<(IReadOnlySet<int> favs, int totalModes)> FavoritesAsync()
    {
        var (fb, fn) = QcUltra2.Favorites;
        var resp = await GetAsync(fb, fn);
        return QcUltra2.ParseFavorites(resp.Payload);
    }

    /// <summary>
    /// Write the favourite-modes bitmask.
    /// bmap: SETGET [31.8] payload=[totalModes, 0x00, maskByte]
    /// </summary>
    public async Task SetFavoritesAsync(IReadOnlySet<int> favSet, int totalModes = 11)
    {
        var (fb, fn) = QcUltra2.Favorites;
        var pkt = BmapProtocol.Build(fb, fn, Op.SetGet, QcUltra2.BuildFavorites(totalModes, favSet));
        var raw = await _t.SendRecvAsync(pkt);
        ThrowIfError(FindResponse(raw, fb, fn));
    }

    /// <summary>
    /// Set spatial audio mode.
    /// bmap: GET [31.10] then SETGET [31.10].
    /// </summary>
    public async Task SetSpatialAsync(SpatialMode mode)
    {
        var s = await AudioSettingsAsync();
        await WriteAudioSettingsAsync(s with { Spatial = mode });
    }


    /// <summary>Auto Play/Pause (pause on ear removal).</summary>
    public async Task<bool> AutoPlayPauseAsync()
    {
        var (fb, fn) = QcUltra2.AutoPlayPause;
        var resp = await GetAsync(fb, fn);
        return resp.Payload.Length > 0 && resp.Payload[0] != 0;
    }

    /// <summary>Toggle auto play/pause.</summary>
    public async Task SetAutoPlayPauseAsync(bool enabled)
    {
        var (fb, fn) = QcUltra2.AutoPlayPause;
        var raw = await _t.SendRecvAsync(BmapProtocol.Build(fb, fn, Op.SetGet, new byte[] { (byte)(enabled ? 1 : 0) }));
        ThrowIfError(FindResponse(raw, fb, fn));
    }

    /// <summary>Auto-answer calls.</summary>
    public async Task<bool> AutoAnswerAsync()
    {
        var (fb, fn) = QcUltra2.AutoAnswer;
        var resp = await GetAsync(fb, fn);
        return resp.Payload.Length > 0 && resp.Payload[0] != 0;
    }

    /// <summary>Toggle auto-answer.</summary>
    public async Task SetAutoAnswerAsync(bool enabled)
    {
        var (fb, fn) = QcUltra2.AutoAnswer;
        var raw = await _t.SendRecvAsync(BmapProtocol.Build(fb, fn, Op.SetGet, new byte[] { (byte)(enabled ? 1 : 0) }));
        ThrowIfError(FindResponse(raw, fb, fn));
    }

    /// <summary>
    /// Power off the headphones.
    /// bmap: START [7.4] payload=[0x00] — device disconnects immediately after.
    /// </summary>
    public async Task PowerOffAsync()
    {
        var (fb, fn) = QcUltra2.Power;
        // Socket closes on the device side after power-off; swallow the IO exception.
        try { await _t.SendRecvAsync(BmapProtocol.Build(fb, fn, Op.Start, new byte[] { 0x00 })); }
        catch { /* expected — device disconnects */ }
    }

    /// <summary>
    /// Enter Bluetooth pairing mode.
    /// bmap: START [4.8] payload=[0x01] — device may disconnect.
    /// </summary>
    public async Task EnterPairingModeAsync()
    {
        var (fb, fn) = QcUltra2.Pairing;
        try { await _t.SendRecvAsync(BmapProtocol.Build(fb, fn, Op.Start, new byte[] { 0x01 })); }
        catch { }
    }

    // ── Low-level helpers ─────────────────────────────────────────────────────

    private async Task<BmapResponse> GetAsync(byte fblock, byte func)
    {
        var pkt  = BmapProtocol.Build(fblock, func, Op.Get);
        var raw  = await _t.SendRecvAsync(pkt);
        var resp = FindResponse(raw, fblock, func);
        ThrowIfError(resp);
        if (resp is null)
            throw new InvalidOperationException($"No response for GET [{fblock}.{func}]");
        if (_debug)
            Console.WriteLine($"[bose] GET [{fblock}.{func}]: {resp.Format()}");
        return resp;
    }

    private async Task WriteAudioSettingsAsync(AudioSettings settings)
    {
        var (fb, fn) = QcUltra2.AudioSettings;
        byte[] payload = QcUltra2.BuildAudioSettings(settings);
        var pkt  = BmapProtocol.Build(fb, fn, Op.SetGet, payload);
        var raw  = await _t.SendRecvAsync(pkt);
        var resp = FindResponse(raw, fb, fn);
        ThrowIfError(resp);
        if (_debug && resp is not null)
            Console.WriteLine($"[bose] SETGET [{fb}.{fn}]: {resp.Format()}");
    }

    private static void ThrowIfError(BmapResponse? resp)
    {
        if (resp?.Operator != Op.Error) return;
        byte code = resp.Payload.Length > 0 ? resp.Payload[0] : (byte)0;
        string name = BmapError.Name(code);
        throw new BoseProtocolException($"Device error: {name} ({resp.Format()})");
    }

    /// <summary>
    /// Find the response packet matching [fblock.func] among all packets in raw.
    /// Skips unsolicited notifications (e.g. [2.3] FuncNotSupp) that may precede
    /// the actual reply. Falls back to the first packet if no match found.
    /// Linear scan over typically 1-3 packets; no upgrade needed.
    /// </summary>
    private static BmapResponse? FindResponse(byte[] raw, byte fblock, byte func)
    {
        var all = BmapProtocol.ParseAll(raw);
        return all.FirstOrDefault(r => r.FBlock == fblock && r.Func == func)
               ?? all.FirstOrDefault();
    }

    public async ValueTask DisposeAsync() => await _transport.DisposeAsync();
}

/// <summary>Thrown when the device returns a BMAP ERROR response.</summary>
public sealed class BoseProtocolException : Exception
{
    public BoseProtocolException(string message) : base(message) { }
}
