# Quiet Rebellion

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Upstream](https://img.shields.io/badge/upstream-aaronsb%2Fbosectl-blue)](https://github.com/aaronsb/bosectl)
[![Devices](https://img.shields.io/badge/Devices-4_supported_·_38_known-green)](docs/architecture.md#device-catalog)
[![Python 3](https://img.shields.io/badge/Python-3-3572A5.svg)](python/)
[![Rust](https://img.shields.io/badge/Rust-1.70+-DEA584.svg)](rust/)
[![C++17](https://img.shields.io/badge/C++-17-f34b7d.svg)](cpp/)
[![Android](https://img.shields.io/badge/Android-8.0%2B-3DDC84.svg)](android/)
[![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D4.svg)](csharp/)
[![Platform: Linux](https://img.shields.io/badge/Platform-Linux-orange.svg)](https://kernel.org)

**Control Bose headphones from Linux, Android, or Windows — no app, no cloud, no account.**

> **CLI and library also known as `bosectl`** (upstream project name).

> **Fork of [aaronsb/bosectl](https://github.com/aaronsb/bosectl).**  
> This fork adds a native **Windows tray app and CLI** (`csharp/`) and an
> **Android app** (`android/`) on top of the original Linux Python/Rust/C++ libraries.  
> All upstream protocol work, device catalog, and library code is unchanged.
>
> ⚠️ **Windows and Android ports are beta.** Core features work well; expect occasional
> rough edges. For stable, well-tested control use the Python or Rust CLI on Linux.

![bosectl CLI](docs/media/screenshot.png)

Libraries in Python, Rust, and C++ implementing the Bose BMAP protocol
over Bluetooth RFCOMM. Full control over noise cancellation, EQ, spatial
audio, button mapping, profiles, and device settings through a direct
connection to the headphones.

> **This is not an exploit.** We use the BMAP protocol's standard SETGET
> operator, which the headphones accept without authentication. No keys
> are extracted, no encryption is broken, no traffic is replayed.

## Supported Devices

| Device | NC Control | EQ | Spatial | Profiles | Buttons | Status |
|--------|-----------|-----|---------|----------|---------|--------|
| **QC Ultra 2 Headphones** | ANC 0–10 slider + Auto-ANC | 3-band | room/head | 7 custom slots | Read-only (remap needs cloud auth) | Verified |
| **QuietComfort 35 / 35 II** | ANR off/high/wind/low | — | — | — | Action remap (VPA/ANC) | Verified |

### Device Roadmap

The library includes a [device catalog](docs/architecture.md#device-catalog)
of all known BMAP-capable Bose products (38 total, 34 without a tested
configuration yet). These are recognized by Bluetooth product ID —
contributions welcome. Selected highlights:

| Device | Codename | Category | PID |
|--------|----------|----------|-----|
| Noise Cancelling Headphones 700 | goodyear | Headphones | `0x4024` |
| QuietComfort 45 | duran | Headphones | `0x4039` |
| QuietComfort Headphones | prince | Headphones | `0x4075` |
| QuietComfort Ultra Headphones | lonestarr | Headphones | `0x4066` |
| QuietComfort Earbuds II | smalls | Earbuds | `0x4064` |
| QuietComfort Ultra Earbuds | scotty | Earbuds | `0x4072` |
| Ultra Open Earbuds | serena | Earbuds | `0x4068` |
| SoundLink Flex | phelps | Speaker | `0xBC59` |
| SoundLink Flex 2 | mathers | Speaker | `0xBC61` |

See [`python/pybmap/catalog.py`](python/pybmap/catalog.py) for the complete list.

Adding a new device is a configuration entry — no library code changes needed.
See [Adding a New Device](docs/architecture.md#adding-a-new-device).

## Quick Start

### Windows (this fork)

System tray app — launch and forget. Auto-detects paired QC Ultra 2 headphones.

Tray features: ANC slider (0–10) + Auto-ANC, Wind Block, Spatial audio,
3-band EQ, Sidetone, Listening modes (with custom slots + Favourites ★),
Multipoint on/off, device switching with names from headphone memory,
Now Playing source in tooltip, Auto Play/Pause, Auto-answer, Device Rename,
Power Off, Bluetooth Pairing Mode.

```powershell
cd csharp\BoseCtl
dotnet run          # tray mode (no args), auto-detects paired headphones
```

Or CLI mode:

```powershell
dotnet run -- --set-mode quiet    # full ANC
dotnet run -- --set-cnc 5         # noise cancellation level 0-10
dotnet run -- --help
```

See **[csharp/README.md](csharp/README.md)** for build, publish, and tray-app docs.

### Android (this fork)

Features: ANC slider (0–10) + Auto-ANC, Spatial audio (Off/Room/Head), Wind Block,
3-band EQ, Sidetone, Multipoint with device names, Listening mode selection
(Quiet/Aware/Immersion/Cinema + custom slots), Favorites (long-press ★),
Auto Play/Pause, Auto Answer, Device Rename, Power Off, Bluetooth Pairing Mode,
Now Playing, persistent notification with quick controls.  
Two built-in themes (Fresh & Clean / Monochrome, Light/Dark) + Material You (Android 12+).

Build and install via Android Studio or:

```bash
# Linux / macOS
cd android
./gradlew assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

```powershell
# Windows
cd android
.\gradlew assembleDebug
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

See **[android/README.md](android/README.md)** for details.

### Linux / Library Usage

```python
import pybmap

with pybmap.connect() as dev:
    print(dev.battery())            # 80
    print(dev.name())               # "Obsidian Countess"
    dev.set_anr("high")             # QC35: full noise cancellation
    dev.set_cnc(8)                  # QC Ultra 2: CNC level 0-10
    dev.set_eq(3, 0, -2)            # Bass +3, mid flat, treble -2
    dev.set_buttons(0x10, 4, 2)     # Remap Action button to ANC
```

```rust
use bmap::connect;

let dev = connect(None, None)?;
println!("{}%", dev.battery()?);    // 80
dev.set_anr("high")?;              // QC35
dev.set_cnc(8)?;                   // QC Ultra 2
```

```cpp
#include "bmap.h"

auto dev = bmap::connect();
std::cout << (int)dev->battery() << "%\n";
dev->set_anr("high");              // QC35
dev->set_cnc(8);                   // QC Ultra 2
```

### CLI Usage

```bash
# Auto-detects paired Bose device
bosectl status              # Show model, battery, mode, settings
bosectl cnc 7               # Noise cancellation level (QC Ultra 2)
bosectl anr high            # Noise cancellation mode (QC35)
bosectl eq 3 0 -2           # EQ: bass/mid/treble
bosectl buttons set ANC     # Remap programmable button
bosectl quiet               # Switch to Quiet mode
```

### Device Catalog API

```python
import pybmap

# Look up any known Bose device by product ID
dev = pybmap.lookup_device(0x4082)
print(dev.name)       # "QuietComfort Ultra Headphones (2nd Gen)"
print(dev.codename)   # "wolverine"

# USB/Bluetooth identification
pybmap.usb_ids(0x4082)    # (0x05A7, 0x4082)
pybmap.modalias(0x4082)   # "bluetooth:v05A7p4082d0000"

# Check support status
pybmap.is_supported(0x4082)  # True — has tested config
pybmap.is_supported(0x4039)  # False — QC45, recognized but untested
pybmap.supported_devices()   # [wolfcastle, baywolf, edith, wolverine]
pybmap.known_devices()       # full catalog
```

## Installation

### From Release Binaries (this fork)

Pre-built files are attached to each [GitHub Release](https://github.com/MadEyez/quietrebellion/releases/latest):

| Platform | File | Notes |
|---|---|---|
| Windows | `bosectl.exe` | Self-contained — no .NET runtime needed |
| Android | `app-release.apk` | Enable *Install from unknown sources* in Android settings |

For Linux use the upstream binaries (see below) or build from source.

### Windows — Build from Source

```powershell
cd csharp\BoseCtl
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# → bin\Release\...\win-x64\publish\bosectl.exe
```

Pair the headphones in Windows Bluetooth settings first. The Bose app must not be
running (it holds the RFCOMM channel).

### Linux — Prerequisites

- **Linux** with BlueZ (standard Bluetooth stack)
- **Bluetooth** adapter (built-in or USB)
- **Bose headphones** paired via `bluetoothctl`

### From Release Binaries (upstream)

> These binaries are from the upstream project and cover Linux only.

```bash
# Download from GitHub releases
curl -LO https://github.com/aaronsb/bosectl/releases/latest/download/bmapctl-rust-linux-x86_64
curl -LO https://github.com/aaronsb/bosectl/releases/latest/download/SHA256SUMS
sha256sum -c SHA256SUMS
chmod +x bmapctl-rust-linux-x86_64
sudo cp bmapctl-rust-linux-x86_64 /usr/local/bin/bmapctl
```

### From Source

```bash
git clone https://github.com/aaronsb/bosectl.git   # upstream (Linux/Python/Rust/C++)
# or clone this fork for Windows + Android additions
cd bosectl
make test          # Run all tests (Python + Rust + C++)
make artifacts     # Build release binaries + SHA256SUMS
```

See `make help` for all targets.

### Pairing

If your headphones aren't already paired:

```bash
bluetoothctl
> scan on
> pair XX:XX:XX:XX:XX:XX
> trust XX:XX:XX:XX:XX:XX
> connect XX:XX:XX:XX:XX:XX
> exit
```

`bosectl` auto-detects paired Bose devices by their BMAP service UUID —
no MAC address configuration needed, even with renamed headphones.

## Architecture

Three libraries sharing the same layered design:

```
Application → BmapConnection → Device Config → Transport → Protocol → Bluetooth RFCOMM
```

- **Protocol** — binary BMAP packet codec
- **Transport** — RFCOMM socket with drain mode for async responses
- **Device Config** — data-only description of each headphone model (addresses, parsers, quirks)
- **BmapConnection** — typed API that dispatches to the right address/parser per device
- **Catalog** — all known Bose BMAP devices with product IDs, codenames, and USB identifiers

Device differences (RFCOMM channel, init packets, feature availability) are
expressed as config data, not code branches. Adding a new device is a
config entry pointing to existing parsers.

Full documentation: **[docs/architecture.md](docs/architecture.md)**

## How It Works

Bose headphones speak **BMAP** (Bose Messaging and Protocol) over
Bluetooth RFCOMM. The protocol is organized into function blocks
(groups of features) and operators (read, write, action).

The key insight: while Bose gates SET (operator 0) behind cloud-mediated
ECDH authentication, **SETGET** (operator 2) and **START** (operator 5)
are unauthenticated on the Settings and AudioModes blocks. This gives
full control over every user-facing setting.

Full protocol reference: **[NOTES.md](NOTES.md)**

### How We Found This

1. Connected over RFCOMM, probed all channels — channel 2 (QC Ultra 2) and 8 (QC35) responded with BMAP
2. Captured Bluetooth HCI traffic while toggling settings in the Bose app
3. DNS-hijacked the cloud API and noticed mode switching still worked — the app uses START, not SET
4. Systematically tested every operator on every function block to map the auth boundary

## Project Structure

```
├── python/pybmap/       # Python library + bosectl CLI  (upstream)
├── rust/src/            # Rust library + bmapctl CLI     (upstream)
├── cpp/src/             # C++ library + bmapctl CLI      (upstream)
├── android/             # Android app — QC Ultra 2       (this fork)
├── csharp/BoseCtl/      # Windows tray app + CLI         (this fork)
├── docs/                # Architecture guide, device docs
├── NOTES.md             # Protocol reverse engineering notes
├── Makefile             # Build, test, release across all languages
└── fixtures/            # Captured protocol data
```

## Building & Releasing

```bash
make test                       # All tests (138 Python, 61 Rust, 53 C++)
make artifacts                  # Build + strip + SHA256SUMS in dist/
make release VERSION=v0.2.0     # Test → build → gh release create
make clean                      # Remove all build artifacts
```

## License

MIT — see [LICENSE](LICENSE) for full text.

- **Upstream code** (`python/`, `rust/`, `cpp/`): © [aaronsb/bosectl](https://github.com/aaronsb/bosectl) contributors
- **This fork** (`android/`, `csharp/`): © 2026 Aaron Bockelie
