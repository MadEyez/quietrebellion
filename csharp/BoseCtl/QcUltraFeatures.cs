/*
 * QcUltraFeatures.cs – Feature address map for Bose QC Ultra Headphones (2nd Gen).
 *
 * Source: bosectl/python/pybmap/devices/qc_ultra2.py
 * Codename: wolverine  |  Platform: OTG-QCC-384  |  Product-ID: 0x4082
 * Firmware tested upstream: 8.2.20+g34cf029
 *
 * Auth notes (from bosectl upstream analysis):
 *   GET    (op 1)  – works on all blocks without auth
 *   SETGET (op 2)  – works on Settings [1.x] and AudioModes [31.x] without auth
 *   START  (op 5)  – works on AudioModes [31.x] without auth
 *   SET    (op 0)  – requires cloud-mediated ECDH auth on all blocks (not implemented)
 *   Preset mode slots 0-3 reject SETGET with Runtime error (firmware lock)
 */

namespace BoseCtl;

/// <summary>
/// All known BMAP feature addresses for QC Ultra 2 (wolverine).
/// Add new devices by implementing another static class with the same shape.
/// </summary>
public static class QcUltra2
{
    // ponytail: RFCOMM channel 2 is hardcoded in all bosectl captures.
    // Upgrade path: SDP lookup on BMAP_UUID if a device uses a different channel.
    public const int RfcommChannel = 2;

    // BMAP Bluetooth SDP service UUID – shared by ALL Bose BMAP devices.
    // ref: bosectl/python/pybmap/catalog.py BMAP_UUID
    public const string BmapServiceUuid = "00000000-deca-fade-deca-deafdecacaff";

    // Bluetooth Modalias product ID (from Windows device properties).
    public const ushort ProductId = 0x4082;

    // ── Feature Addresses [fblock, func] ─────────────────────────────────────
    //
    // bmap: [0.5]  GET → firmware version string (ASCII)
    public static readonly (byte fblock, byte func) Firmware     = (0, 5);

    // bmap: [1.2]  GET → [flag, ...name_utf8]  (byte 0 is a flag, name starts at byte 1)
    public static readonly (byte fblock, byte func) ProductName  = (1, 2);

    // bmap: [1.5]  GET → [max, current, ?]  max = reported_max+1, current = CNC level
    public static readonly (byte fblock, byte func) Cnc          = (1, 5);

    // bmap: [1.7]  GET → 4-byte groups: [min, max, current, band_id] per EQ band
    //              SETGET payload: [value_signed_byte, band_id]  (bands: 0=Bass,1=Mid,2=Treble)
    public static readonly (byte fblock, byte func) Eq           = (1, 7);

    // bmap: [2.2]  GET → [percent]  battery level 0–100
    public static readonly (byte fblock, byte func) Battery      = (2, 2);

    // bmap: [31.1] START (no payload) → drains multiple STATUS [31.6] packets, one per mode
    public static readonly (byte fblock, byte func) GetAllModes  = (31, 1);

    // bmap: [31.3] GET   → [mode_index]
    //              START → [mode_index, announce_flag]  switches active mode
    public static readonly (byte fblock, byte func) CurrentMode  = (31, 3);

    // bmap: [4.4]  GET → paired device list: [count_or_flags, mac1(6), mac2(6), ...]
    public static readonly (byte fblock, byte func) PairedDevices = (4, 4);

    // bmap: [4.12] START → route audio to a different device
    //              payload: [0x82, mac0, mac1, mac2, mac3, mac4, mac5]
    //              0x82 = bit7 (UP direction) | bit1 (device slot)
    public static readonly (byte fblock, byte func) Routing = (4, 12);

    // bmap: [5.1]  GET → active audio source
    //              layout: [supported_hi, supported_lo, source_type, ...mac_if_bt]
    //              source_type: 0=none, 1=bluetooth (6-byte MAC follows), 2=auxiliary
    public static readonly (byte fblock, byte func) Source = (5, 1);

    // bmap: [1.10] GET  → payload[0] bit-1 (0x02) = multipoint enabled
    //              SETGET → [1] on / [0] off
    public static readonly (byte fblock, byte func) Multipoint = (1, 10);

    // bmap: [1.11] GET  → [persist, level]  level: 0=off 1=high 2=medium 3=low
    //              SETGET → [1, level]   (persist=1)
    public static readonly (byte fblock, byte func) Sidetone = (1, 11);

    // bmap: [31.10] GET    → [cnc_level, auto_cnc, spatial, wind_block, anc_toggle]
    //               SETGET → same 5-byte payload to write audio settings
    public static readonly (byte fblock, byte func) AudioSettings = (31, 10);

    // ── Sidetone constants (from bosectl constants.py SIDETONE_NAMES) ─────────
    public static readonly IReadOnlyDictionary<int, string> SidetoneNames =
        new Dictionary<int, string> { {0,"off"}, {1,"high"}, {2,"medium"}, {3,"low"} };
    public static readonly IReadOnlyDictionary<string, int> SidetoneByName =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        { {"off",0}, {"high",1}, {"medium",2}, {"med",2}, {"low",3} };

    // ── Preset Mode Index Map ─────────────────────────────────────────────────
    // Preset slots 0-3 are firmware-locked (SETGET rejected with Runtime error).
    // Custom/editable slots start at index 4.
    public static readonly IReadOnlyDictionary<int, string> ModeNames = new Dictionary<int, string>
    {
        { 0, "quiet"     },   // Full ANC
        { 1, "aware"     },   // Transparency / ambient
        { 2, "immersion" },   // Spatial audio + head tracking
        { 3, "cinema"    },   // Spatial audio, fixed stage
    };

    public static readonly IReadOnlyDictionary<string, int> ModeIndexByName =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "quiet",     0 },
            { "aware",     1 },
            { "immersion", 2 },
            { "cinema",    3 },
        };

    // bmap: [31.6] STATUS (returned by drain after GetAllModes START [31.1])
    //       Each STATUS carries one mode's 48-byte configuration.
    public static readonly (byte fblock, byte func) ModeConfigStatus = (31, 6);

    // ── Mode Config Parser (name only) ────────────────────────────────────────

    /// <summary>
    /// Extract (modeIndex, name) from a STATUS [31.6] payload (≥ 38 bytes).
    /// STATUS layout (from bosectl qc_ultra2.py parse_mode_config_48):
    ///   [0]    modeIndex
    ///   [1–2]  voicePrompt bytes
    ///   [3–5]  editable/configured flags
    ///   [6–37] modeName (32 bytes, null-terminated UTF-8)
    ///   [38–47] cnc_level, auto_cnc, spatial, wind_block, …, anc_toggle
    /// </summary>
    public static (int idx, string name)? ParseModeConfigBasic(byte[] p)
    {
        if (p.Length < 38) return null;
        int idx     = p[0];
        int nameEnd = Array.IndexOf(p, (byte)0, 6);
        if (nameEnd < 6 || nameEnd > 38) nameEnd = 38;
        string name = System.Text.Encoding.UTF8.GetString(p, 6, nameEnd - 6).Trim();
        return (idx, name.Length > 0 ? name : $"custom({idx})");
    }

    /// <summary>Parse battery GET response. Returns 0–100.</summary>
    public static int ParseBattery(byte[] p) => p.Length > 0 ? p[0] : 0;

    /// <summary>
    /// Parse firmware GET response. Raw ASCII string.
    /// Example: "8.2.20+g34cf029"
    /// </summary>
    public static string ParseFirmware(byte[] p) =>
        System.Text.Encoding.ASCII.GetString(p).TrimEnd('\0');

    /// <summary>
    /// Parse product name GET response.
    /// Byte 0 is a flag; name is the UTF-8 remainder.
    /// </summary>
    public static string ParseProductName(byte[] p) =>
        p.Length > 1 ? System.Text.Encoding.UTF8.GetString(p, 1, p.Length - 1).TrimEnd('\0') : "";

    /// <summary>
    /// Parse CNC GET [1.5] response.
    /// Returns (current, max) tuple. Max = payload[0]-1, current = payload[1].
    /// </summary>
    public static (int current, int max) ParseCnc(byte[] p) =>
        p.Length >= 3 ? (p[1], p[0] - 1) : (0, 10);

    /// <summary>
    /// Parse AudioModesSettingsConfig GET [31.10] response (5 bytes).
    /// Returns structured audio settings.
    /// </summary>
    public static AudioSettings ParseAudioSettings(byte[] p)
    {
        if (p.Length < 5)
            return new AudioSettings(0, false, SpatialMode.Off, false, false);
        return new AudioSettings(
            CncLevel:   p[0],
            AutoCnc:    p[1] != 0,
            Spatial:    (SpatialMode)p[2],
            WindBlock:  p[3] != 0,
            AncToggle:  p[4] != 0);
    }

    /// <summary>
    /// Build AudioModesSettingsConfig SETGET [31.10] payload (5 bytes).
    /// Write with SETGET op – no auth required on QC Ultra 2.
    /// </summary>
    public static byte[] BuildAudioSettings(AudioSettings s) =>
        new byte[] { (byte)s.CncLevel, (byte)(s.AutoCnc ? 1 : 0), (byte)s.Spatial,
                     (byte)(s.WindBlock ? 1 : 0), (byte)(s.AncToggle ? 1 : 0) };

    /// <summary>
    /// Parse EQ GET [1.7] response.
    /// 4-byte groups per band: [min, max, current, band_id].
    /// Signed bytes use two's complement (>= 128 → value - 256).
    /// </summary>
    private static readonly string[] BandNames = new[] { "Bass", "Mid", "Treble" };

    public static List<EqBand> ParseEq(byte[] p)
    {
        var bands = new List<EqBand>();
        for (int i = 0; i + 3 < p.Length; i += 4)
        {
            int min     = p[i]     >= 128 ? p[i]     - 256 : p[i];
            int max     = p[i + 1] >= 128 ? p[i + 1] - 256 : p[i + 1];
            int current = p[i + 2] >= 128 ? p[i + 2] - 256 : p[i + 2];
            int bandId  = p[i + 3];
            string name = bandId < BandNames.Length ? BandNames[bandId] : $"Band{bandId}";
            bands.Add(new EqBand(bandId, name, min, max, current));
        }
        return bands;
    }

    /// <summary>
    /// Parse Sidetone GET [1.11] response.
    /// Layout: [persist_flag, level]  where level 0=off 1=high 2=medium 3=low.
    /// </summary>
    public static int ParseSidetone(byte[] p) => p.Length >= 2 ? p[1] : 0;

    /// <summary>
    /// Parse Multipoint GET [1.10] response.
    /// Bit 1 (0x02) of payload[0] = enabled.
    /// </summary>
    public static bool ParseMultipoint(byte[] p) =>
        p.Length > 0 && (p[0] & 0x02) != 0;

    // ── Builders ─────────────────────────────────────────────────────────────

    /// <summary>Build Sidetone SETGET [1.11] payload: [persist=1, level].</summary>
    public static byte[] BuildSidetone(int level) =>
        new byte[] { 1, (byte)level };

    /// <summary>Build a boolean toggle SETGET payload (e.g. multipoint).</summary>
    public static byte[] BuildToggle(bool enabled) =>
        new byte[] { (byte)(enabled ? 1 : 0) };

    /// <summary>
    /// Build EQ band SETGET [1.7] payload: [value_as_signed_byte, band_id].
    /// value range: -10..+10 stored as two's complement byte.
    /// </summary>
    public static byte[] BuildEqBand(int value, int bandId) =>
        new byte[] { (byte)(value & 0xFF), (byte)bandId };

    /// <summary>Build device name SETGET [1.2] payload (raw UTF-8, no flag byte).</summary>
    public static byte[] BuildDeviceName(string name) =>
        System.Text.Encoding.UTF8.GetBytes(name);

    // ── Multipoint / Source / Routing ─────────────────────────────────────────

    /// <summary>
    /// Parse active audio source GET [5.1].
    /// Layout: [supported_hi, supported_lo, source_type, ...mac_if_bt]
    /// Returns (source_type_name, mac_string_or_null).
    /// </summary>
    public static (string type, string? mac) ParseSource(byte[] p)
    {
        if (p.Length < 3) return ("none", null);
        string typeName = p[2] switch { 1 => "bluetooth", 2 => "auxiliary", _ => "none" };
        string? mac = null;
        if (p[2] == 1 && p.Length >= 9)
            mac = string.Join(":", p.Skip(3).Take(6).Select(b => b.ToString("X2")));
        return (typeName, mac);
    }

    /// <summary>
    /// Parse paired device list GET [4.4].
    /// First byte is a count/flags field; remainder is back-to-back 6-byte MACs.
    /// Skips all-zero entries.
    /// </summary>
    public static List<string> ParsePairedDevices(byte[] p)
    {
        var result = new List<string>();
        for (int i = 1; i + 5 < p.Length; i += 6)
        {
            if (p[i] == 0 && p[i+1] == 0 && p[i+2] == 0) continue; // empty slot
            result.Add(string.Join(":", p.Skip(i).Take(6).Select(b => b.ToString("X2"))));
        }
        return result;
    }

    /// <summary>
    /// Build ROUTING START [4.12] payload.
    /// Payload: [0x82, mac0…mac5]
    /// 0x82 = bit7 (UP direction) | bit1 (device slot) – from bosectl build_routing.
    /// </summary>
    public static byte[] BuildRouting(string mac)
    {
        byte[] macBytes = mac.Split(':').Select(b => Convert.ToByte(b, 16)).ToArray();
        if (macBytes.Length != 6) throw new ArgumentException("Invalid MAC: " + mac);
        var payload = new byte[7];
        payload[0] = 0x82;
        Array.Copy(macBytes, 0, payload, 1, 6);
        return payload;
    }
}

// ── Value types ───────────────────────────────────────────────────────────────

public enum SpatialMode : byte { Off = 0, Room = 1, Head = 2 }

public sealed record AudioSettings(
    int CncLevel,
    bool AutoCnc,
    SpatialMode Spatial,
    bool WindBlock,
    bool AncToggle);

public sealed record EqBand(
    int BandId,
    string Name,
    int MinVal,
    int MaxVal,
    int Current);

