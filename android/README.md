# Quiet Rebellion – Android App

> ⚠️ **Pre-alpha.** Core features work on QC Ultra 2 (wolverine), but expect
> rough edges: occasional disconnects, UI states not always in sync with the
> headphone, no error recovery UI. The Python CLI is the stable reference
> implementation — use this if you want a native Android UI and are OK
> with filing bugs.

Android companion app for Quiet Rebellion. Controls Bose QC Ultra Headphones (2nd Gen)
directly over Bluetooth RFCOMM without the Bose app, without a cloud account,
and without root.

## Features

- Noise control level (CNC 0–10) + Auto-CNC toggle
- Spatial audio (Off / Room / Head)
- Wind Block
- 3-band EQ (Bass / Mid / Treble)
- Sidetone
- Multipoint connection + device switching
- Listening mode selection (Quiet / Aware / Immersion / Cinema + custom slots)
- Device rename
- **Foreground service** – connection stays alive when you leave the app
- **Home screen widget** – battery, mode, ANC toggle, Next Mode button

## Requirements

- Android 8.0+ (API 26)
- Bose QC Ultra Headphones (2nd Gen, codename `wolverine`, Product-ID `0x4082`)
- Headphones already paired via Android Bluetooth settings
- **JDK 17+** to build (AGP 8.5 requires JDK 11+, compileOptions target JVM 17)

## Build

### Linux / macOS

```bash
cd android
./gradlew assembleDebug
# APK: app/build/outputs/apk/debug/app-debug.apk
```

### Windows

```powershell
cd android
.\gradlew assembleDebug
# APK: app\build\outputs\apk\debug\app-debug.apk
```

> **JDK version:** Gradle will use the JDK on `PATH`. If the build fails with
> _"Dependency requires at least JVM runtime version 11"_, set `JAVA_HOME` to a
> JDK 17+ installation before running Gradle:
> ```powershell
> $env:JAVA_HOME = "C:\Program Files\Eclipse Adoptium\jdk-21.0.6.7-hotspot"  # example
> $env:PATH = "$env:JAVA_HOME\bin;$env:PATH"
> .\gradlew assembleDebug
> ```

### Deploy via ADB

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

Or open the `android/` folder in **Android Studio** and run directly on device
(Studio manages its own bundled JDK).

## How it connects

Three strategies are tried in order, first success wins:

1. **Reflection `createInsecureRfcommSocket(int)`** – goes through the
   Android Bluetooth service, bypasses OEM SDP quirks. Works on most devices.
2. **Native JNI `AF_BLUETOOTH` socket** – direct Linux kernel socket,
   identical to the Python `socket.connect((mac, 2))` path.
   Blocked by SELinux on unrooted devices → falls through to 3.
3. **SDP fallback** (`createInsecureRfcommSocketToServiceRecord`) –
   standard Android API; some OEM stacks resolve to the wrong channel.

RFCOMM channel 2 is hardcoded (same as all other Quiet Rebellion implementations).
See [`BluetoothTransport.kt`](app/src/main/kotlin/net/quietrebellion/BluetoothTransport.kt)
for details and the `ponytail:` comments on known ceilings.

## Architecture

```
MainActivity ──► BoseService (ForegroundService)
     │                │
     └──► BoseConnection ──► BluetoothTransport ──► RFCOMM ch 2
               │
               └──► BmapProtocol / QcUltra2 (codec + device constants)
```

- **`BmapProtocol`** – packet encode/decode (port of `protocol.py` / `BmapProtocol.cs`)
- **`QcUltra2`** – feature addresses, parsers, builders for the wolverine device
- **`BoseConnection`** – typed high-level API (port of `connection.py` / `BoseConnection.cs`)
- **`BluetoothTransport`** – RFCOMM socket with drain mode for multi-packet responses
- **`BoseService`** – keeps connection alive in background, drives notification + widget
- **`SoundCtlWidget`** – 4×1 home screen widget, reuses `BoseService` broadcast actions

## Permissions

| Permission | Why |
|---|---|
| `BLUETOOTH` / `BLUETOOTH_ADMIN` | Classic BT socket (API ≤ 30 / OEM) |
| `BLUETOOTH_CONNECT` | Runtime grant required on API 31+ |
| `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_CONNECTED_DEVICE` | Keep service alive |
| `POST_NOTIFICATIONS` | Persistent status notification |

## License

MIT – same as the parent project.
