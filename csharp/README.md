# bosectl – Windows C# Prototype

> ⚠️ **Pre-alpha.** Tray app and CLI run on QC Ultra 2 (wolverine), but this
> is early-stage code: no installer, no auto-start, no persistent settings,
> connection errors surface as tray tooltip text rather than a proper UI.
> The Python CLI is the stable reference implementation — use this if you
> want native Windows integration and are OK with rough edges.

Native Windows console tool for controlling **Bose QC Ultra Headphones** over
Bluetooth without the official Bose app.  
Communicates directly via the BMAP (Bose Messaging and Protocol) over RFCOMM.

---

## Requirements

| Requirement | Details |
|---|---|
| OS | Windows 10 (2004 / 19041) or Windows 11 |
| .NET SDK | 7.0 or newer ([download](https://dotnet.microsoft.com/download)) |
| Bluetooth | Device must be **paired and connected** via Windows Bluetooth settings |
| No Bose app | The official Bose app must **not** be open (it holds the RFCOMM channel) |

---

## Build

```powershell
cd csharp\BoseCtl
dotnet build
```

Run directly:
```powershell
dotnet run
```

Or build a self-contained portable .exe (no .NET install needed):
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# Output: bin\Release\net7.0-windows10.0.19041.0\win-x64\publish\bosectl.exe
```

---

## Usage

```
bosectl                          Print battery, mode, audio settings and EQ
bosectl --set-mode quiet         Switch to Quiet (full ANC)
bosectl --set-mode aware         Switch to Aware (transparency)
bosectl --set-mode immersion     Switch to Immersion (spatial + head tracking)
bosectl --set-mode cinema        Switch to Cinema (spatial, fixed stage)
bosectl --set-cnc <0-10>         Set noise cancellation level
bosectl --debug                  Hex-dump every BMAP packet sent/received
bosectl --help                   Show help
```

### Example output

```
Searching for Bose device…
Found: Bose QC Ultra Headphones  [XX:XX:XX:XX:XX:XX]
Connected (RFCOMM/BMAP).

Device      : Bose QC Ultra Headphones
Firmware    : 8.2.20+g34cf029
Battery     : 87 %
Mode        : aware
ANC         : on
CNC level   : 5  (0=max ANC, 10=max ambient)
Wind block  : off
Auto CNC    : off
Spatial     : off
EQ          :
              Bass      +0 dB  (range -10…+10)
              Mid       +0 dB  (range -10…+10)
              Treble    +0 dB  (range -10…+10)
```

---

## BMAP Protocol Reference

All communication uses the **BMAP** protocol over RFCOMM channel 2.

### Packet format

```
Byte 0   fblock_id    Function block (e.g. 2 = Battery, 31 = AudioModes)
Byte 1   func_id      Function within the block
Byte 2   flags        (device_id << 6) | (port_num << 4) | (operator & 0x0F)
                      device_id=0, port_num=0 for host→device
Byte 3   length       Payload byte count
Byte 4…  payload      Function-specific data
```

### Operators

| Code | Name | Direction | Auth |
|---|---|---|---|
| 0 | SET | host→device | Required (ECDH) |
| 1 | GET | host→device | None |
| 2 | SETGET | host→device | None on [1.x] and [31.x] |
| 3 | STATUS | device→host | — |
| 4 | ERROR | device→host | — |
| 5 | START | host→device | None on [31.x] |
| 6 | RESULT | device→host | — |
| 7 | PROCESSING | device→host | — |

### Feature addresses (QC Ultra 2 / wolverine / 0x4082)

| Feature | Address | GET payload | Notes |
|---|---|---|---|
| Firmware | [0.5] | ASCII string | |
| Product name | [1.2] | `[flag, ...utf8]` | byte 0 is a flag |
| CNC settings | [1.5] | `[max, current, ?]` | max = reported_max+1 |
| EQ | [1.7] | 4-byte groups per band `[min, max, current, band_id]` | signed bytes |
| Battery | [2.2] | `[percent]` | 0–100 |
| Get all modes | [31.1] | START → multiple STATUS [31.6] | drain required |
| Current mode | [31.3] | `[mode_index]` | START to switch: `[idx, announce]` |
| Audio settings | [31.10] | `[cnc_level, auto_cnc, spatial, wind_block, anc_toggle]` | SETGET to write |

### Preset mode indices (firmware-locked, cannot SETGET)

| Index | Name | Description |
|---|---|---|
| 0 | quiet | Full ANC |
| 1 | aware | Transparency / ambient pass-through |
| 2 | immersion | Spatial audio + head tracking |
| 3 | cinema | Spatial audio, fixed stage |

### BMAP Service UUID

`00000000-deca-fade-deca-deafdecacaff`  
Shared by all Bose BMAP-capable devices. Used for SDP discovery.

---

## Architecture

```
Program.cs                  CLI entry point (args → action)
TrayApp.cs                  WinForms system-tray UI (NotifyIcon, submenus)
├── DeviceDiscovery.cs      Windows BT enumeration + SDP UUID filter
├── WinsockRfcommTransport.cs  Raw AF_BTH/Winsock2 RFCOMM layer (send/recv/drain)
├── RfcommTransport.cs      (alternative) WinRT StreamSocket layer
├── BoseConnection.cs       High-level feature methods (battery, mode, EQ…)
├── QcUltraFeatures.cs      QC Ultra 2 feature map, addresses, parsers, builders
└── BmapProtocol.cs         Stateless packet encoder/decoder + op/error constants
```

Adding a new Bose device:
1. Add a new `*Features.cs` with the device's BMAP addresses and parsers.
2. Update `DeviceDiscovery.cs` to recognize the product ID.
3. Pass the new features class to `BoseConnection` (or create a subclass).

---

## Known Issues and Limitations

| Issue | Details |
|---|---|
| Device must be connected | Windows BT API requires the device to be actively connected for SDP query. Paired-but-idle headphones will not be found. Turn them on and connect via Windows sound settings first. |
| Bose app conflict | If the Bose app is running and holds the RFCOMM channel, the connection will fail with a socket error. Close the app. |
| RFCOMM channel hardcoded | Channel 2 is used for all devices. If Bose ships a firmware that moves the BMAP service to a different channel, SDP discovery (already in DeviceDiscovery.cs) will still find it, but the fallback path would need updating. |
| SET operations require auth | Writing arbitrary BMAP values (op 0 SET) requires a cloud-mediated ECDH handshake not implemented here. SETGET on AudioModes [31.x] and Settings [1.x] works without auth (verified by bosectl upstream). |
| Preset modes are firmware-locked | Modes 0–3 (quiet/aware/immersion/cinema) reject SETGET with a Runtime error. Use mode_switch (START [31.3]) to change between them. |
| QC Ultra 1 / other models | Product ID 0x4066 (lonestarr, first-gen QC Ultra) has not been tested. It likely uses the same protocol but different feature offsets. |
| Windows 10 < 19041 | The WinRT `GetRfcommServicesForIdAsync` with `BluetoothCacheMode.Uncached` requires Windows 10 2004 (build 19041) or later. |

---

## Tested Devices

| Device | Product ID | Firmware | Status |
|---|---|---|---|
| QC Ultra Headphones (2nd Gen / wolverine) | 0x4082 | 8.2.20+ | ✓ Tested |
| QC Ultra Earbuds (2nd Gen / edith) | 0x4062 | unknown | Should work (same feature map) |


---

## Open Items

- [ ] Automatic Windows startup via registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- [ ] Custom EQ preset save/load (JSON in `%APPDATA%\bosectl\presets.json`)
- [ ] Background RFCOMM listener for unsolicited STATUS events (mode changes, battery updates)
- [ ] QC Ultra 1 (0x4066 / lonestarr) compatibility testing
- [ ] Installer (MSIX or WiX) as alternative to portable exe

