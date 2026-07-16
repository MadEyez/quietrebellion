/*
 * Program.cs – quiet-rebellion entry point.
 *
 * No args    → Tray mode (WinForms NotifyIcon, no console window)
 * Any args   → Console mode (allocates a console, then runs the CLI as before)
 *
 * Console mode usage:
 *   quiet-rebellion                          print battery + mode + audio settings
 *   quiet-rebellion --set-mode quiet         switch to Quiet (full ANC)
 *   quiet-rebellion --set-mode aware         switch to Aware (transparency)
 *   quiet-rebellion --set-mode immersion     switch to Immersion (spatial + head tracking)
 *   quiet-rebellion --set-mode cinema        switch to Cinema (spatial, fixed stage)
 *   quiet-rebellion --set-cnc <0-10>         set noise cancellation level
 *   quiet-rebellion --debug                  enable hex-dump of all BMAP packets
 *   quiet-rebellion --help                   show this help
 */

using System.Runtime.InteropServices;
using System.Windows.Forms;
using BoseCtl;

// ─── Route: tray vs console ───────────────────────────────────────────────────

if (args.Length == 0)
{
    // Tray mode – no console window needed.
    Application.SetHighDpiMode(HighDpiMode.SystemAware);
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new TrayApp());
    return 0;
}

// Console mode – allocate a console window so output is visible.
[DllImport("kernel32.dll")] static extern bool AllocConsole();
[DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
if (!AttachConsole(-1))   // -1 = ATTACH_PARENT_PROCESS
    AllocConsole();        // fallback: create a new console window

// ─── Argument parsing ──────────────────────────────────────────────────────────
bool   debug   = args.Contains("--debug");
bool   help    = args.Contains("--help") || args.Contains("-h");
string? setMode = ArgValue(args, "--set-mode");
string? setCnc  = ArgValue(args, "--set-cnc");

if (help)
{
    Console.WriteLine("""
        quiet-rebellion – Bose QC Ultra Windows control tool

        Usage:
          quiet-rebellion [options]

        Options:
          (none)               Launch system tray control
          --set-mode <name>    Switch mode: quiet | aware | immersion | cinema
          --set-cnc  <0-10>   Set noise cancellation level
          --debug              Hex-dump every BMAP packet sent/received
          --help               Show this help

        The headphones must be paired and connected via Windows Bluetooth.
        No Bose app or account required.
        """);
    return 0;
}

// ─── Discovery ────────────────────────────────────────────────────────────────
Console.WriteLine("Searching for Bose device…");
var device = await DeviceDiscovery.FindAsync(verbose: debug);
if (device is null)
{
    Console.Error.WriteLine(
        "No BMAP-capable Bose device found.\n" +
        "Make sure the headphones are paired AND connected via Windows Bluetooth settings.");
    return 1;
}
Console.WriteLine($"Found: {device.Name}  [{device.MacAddress}]");

// ─── Connect ──────────────────────────────────────────────────────────────────
await using var conn = await BoseConnection.ConnectAsync(device, debug);
Console.WriteLine("Connected (RFCOMM/BMAP).\n");

// ─── Execute command or print status ─────────────────────────────────────────
try
{
    if (setMode is not null)
    {
        if (QcUltra2.ModeIndexByName.TryGetValue(setMode, out int idx))
        {
            Console.WriteLine($"Setting mode → {setMode}");
            await conn.SetModeByIndexAsync(idx);
        }
        else
        {
            Console.Error.WriteLine(
                $"Unknown mode '{setMode}'. Valid: {string.Join(", ", QcUltra2.ModeIndexByName.Keys)}");
            return 1;
        }
        Console.WriteLine("Done.");
    }
    else if (setCnc is not null)
    {
        if (!int.TryParse(setCnc, out int level))
        {
            Console.Error.WriteLine($"--set-cnc requires an integer 0–10, got: {setCnc}");
            return 1;
        }
        Console.WriteLine($"Setting CNC level → {level}");
        await conn.SetCncLevelAsync(level);
        Console.WriteLine("Done.");
    }
    else
    {
        await PrintStatusAsync(conn);
    }
}
catch (BoseProtocolException ex)
{
    Console.Error.WriteLine($"Protocol error: {ex.Message}");
    return 2;
}
catch (TimeoutException ex)
{
    Console.Error.WriteLine($"Timeout: {ex.Message}");
    return 2;
}

return 0;

// ─── Helpers ──────────────────────────────────────────────────────────────────

static async Task PrintStatusAsync(BoseConnection conn)
{
    int    battery = await conn.BatteryAsync();
    int    modeIdx = await conn.ModeIndexAsync();
    var    audio   = await conn.AudioSettingsAsync();
    var    eq      = await conn.EqAsync();
    string fw      = await conn.FirmwareAsync();
    string name    = await conn.DeviceNameAsync();

    // Resolve mode name (includes custom slots)
    var modeNames = await conn.GetAllModeNamesAsync();
    string modeName = modeNames.TryGetValue(modeIdx, out var n) ? n : $"custom({modeIdx})";

    Console.WriteLine($"Device      : {name}");
    Console.WriteLine($"Firmware    : {fw}");
    Console.WriteLine($"Battery     : {battery} %");
    Console.WriteLine($"Mode        : {modeName}");
    Console.WriteLine($"ANC         : {(audio.AncToggle ? "on" : "off")}");
    Console.WriteLine($"CNC level   : {audio.CncLevel}  (0=max ANC, 10=max ambient)");
    Console.WriteLine($"Wind block  : {(audio.WindBlock ? "on" : "off")}");
    Console.WriteLine($"Auto CNC    : {(audio.AutoCnc ? "on" : "off")}");
    Console.WriteLine($"Spatial     : {audio.Spatial.ToString().ToLowerInvariant()}");

    try
    {
        bool app = await conn.AutoPlayPauseAsync();
        Console.WriteLine($"Auto pause  : {(app ? "on" : "off")}");
    } catch { }
    try
    {
        bool aa = await conn.AutoAnswerAsync();
        Console.WriteLine($"Auto answer : {(aa ? "on" : "off")}");
    } catch { }

    if (eq.Count > 0)
    {
        Console.WriteLine("EQ          :");
        foreach (var b in eq)
            Console.WriteLine($"              {b.Name,-8} {b.Current,+4} dB" +
                              $"  (range {b.MinVal:+0;-0;0}…{b.MaxVal:+0;-0;0})");
    }

    if (battery <= 20)
        Console.WriteLine($"\n[!] Low battery: {battery} %");
}

static string? ArgValue(string[] a, string flag)
{
    int i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}
