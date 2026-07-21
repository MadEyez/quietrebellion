# Bose QC Ultra 2 — Reverse Engineering Notes

## TL;DR

**We can control the headphones from Linux without the Bose app, Bose account, or cloud.**

The AudioModes function block (block 31) accepts the `START` operator without
authentication. This lets us switch ANC modes, read battery, read all device info,
and more — all over a direct Bluetooth RFCOMM connection.

The SETGET operator on AudioModes and Settings blocks also works without authentication,
giving full control over CNC level, EQ, spatial audio, wind block, device name, and more.

## Device Info
- Product: Bose QC Ultra 2 HP (codename "wolverine")
- Platform: OTG-QCC-384 (Qualcomm QCC chipset)
- Firmware: 8.2.20+g34cf029
- BT MAC: 68:F2:1F:XX:XX:XX
- Serial: 085958TXXXXXXXXXX
- Custom name: "Fargo"
- Product ID: 0x4082, Variant: 0x01

## BMAP Protocol
- Version: 1.1.0
- Transport: Bluetooth SPP over RFCOMM channel 2
- Packet format: `[fblock_id, function_id, flags, payload_length, ...payload]`
- Flags byte: `(device_id << 6) | (port_num << 4) | (operator & 0x0F)`
- Operators: SET=0, GET=1, SET_GET=2, STATUS=3, ERROR=4, START=5, RESULT=6, PROCESSING=7

## What Works Without Authentication

### Reading (GET operator — all blocks)
Everything can be read without auth:
- Battery level, firmware version, serial number, product name
- Current ANC mode, EQ settings, connected devices
- All device info across all function blocks

### Writing — AudioModes (Block 31, START operator)

**This is the breakthrough.** The START operator on block 31 is unauthenticated.

#### Changing ANC/Audio Mode
```
Packet: [31, 3, 0x05, 2, MODE_INDEX, VOICE_PROMPT]
  - Block 31 (AudioModes), Function 3 (CurrentMode), Operator START (5)
  - MODE_INDEX: which mode to activate (see table below)
  - VOICE_PROMPT: 0=silent, 1=play voice prompt
  - Response: RESULT with the new mode index on success
```

#### Available Audio Modes
| Index | Name       | Description                    | Config byte |
|-------|------------|--------------------------------|-------------|
| 0     | Quiet      | Full active noise cancellation | 0x01        |
| 1     | Aware      | Transparency / passthrough     | 0x02        |
| 2     | Immersion  | Spatial audio immersive        | 0x22        |
| 3     | Cinema     | Spatial audio cinema           | 0x24        |
| 4     | (custom)   | User-created slot              | 0x0a        |
| 5–10  | None       | Empty/custom slot              | 0x00        |

#### Other Unauthenticated START Commands (Block 31)
| Function | Name       | Notes |
|----------|------------|-------|
| [31.1]   | GetAll     | Returns PROCESSING — triggers full state dump |
| [31.3]   | CurrentMode| **Mode switching — confirmed working** |
| [31.6]   | ModeConfig | Returns all mode configs as STATUS messages |
| [31.9]   | Reset      | Accepts START (InvalidData with empty payload) |

#### Unauthenticated START in Other Blocks
| Function | Name    | Status | Notes |
|----------|---------|--------|-------|
| [7.1]    | Control.GetAll | PROCESSING | Triggers control state dump |
| [7.4]    | Control.Power  | **RESULT** | **0=power off, 1=power on** |
| [4.1]    | DevMgmt.Connect | InvalidData | Needs device MAC — may initiate BT connection |
| [4.8]    | DevMgmt.PairingMode | **RESULT** | **0x01=enable, 0x00=disable** |
| [4.12]   | DevMgmt.Routing | **RESULT** | **Switch active multipoint device** (see below) |
| [5.1]    | AudioMgmt.Source | GET only | **Query active audio source** (see below) |
| [5.3]    | AudioMgmt.Control | **PROCESSING/RESULT** | **Play/pause/skip confirmed** — requires active A2DP stream (ERROR 06 without stream) |

#### AudioModes SETGET — Full Config Control (No Auth!)

SETGET on AudioModes block 31 is completely unauthenticated.
Preset modes (0-4) are firmware-locked (return Runtime error 8), but custom mode
slots (5-10) accept full configuration changes via SETGET. Combined with
CurrentMode START to switch to the custom mode, this gives us **complete control
over CNC level, spatial audio, wind block, and ANC** — all without any auth.

| Function | Operator | Status | Notes |
|----------|----------|--------|-------|
| [31.6] ModeConfig | SETGET | **WORKS on modes 5-10** | Full config: CNC, spatial, wind, ANC |
| [31.6] ModeConfig | SET | Error 5 (auth) | SET is auth-gated, but SETGET isn't |
| [31.8] Favorites | SETGET | Echo only | Read-only without auth |
| [31.4] DefaultMode | SETGET | No response | Both GET and SETGET blocked |
| [31.3] CurrentMode | SETGET | Error 5 (auth) | But START works for switching |

##### ModeConfig SETGET Payload Format (40 bytes)

Built by `FBlockAudioModesKt.createAudioModesConfigSetGetPayload()`:
```
Offset  Size  Field              Values
0       1     modeIndex          5-10 (custom slots only)
1-2     2     voicePrompt        (byte1, byte2) — see AudioModesPrompt enum
3-34    32    modeName           UTF-8, null-padded to 32 bytes
35      1     cncLevel           0-10 (noise cancellation intensity)
36      1     autoCNCEnabled     0=off, 1=on
37      1     spatialAudioType   0=off, 1=fixedToRoom, 2=fixedToHead
38      1     windBlockEnabled   0=off, 1=on
39      1     ancToggleEnabled   0=off, 1=on
```

##### ModeConfig STATUS Response Format (48 bytes)

The firmware adds 3 flag bytes and extra config fields:
```
Offset  Size  Field              Notes
0       1     modeIndex
1-2     2     voicePrompt
3-5     3     flags              [3]=isUserEditable, [4]=isConfigured, [5]=?
6-37    32    modeName
38-39   2     ?                  Always 0 for custom modes
40-41   2     ?                  Mode-type specific (0x1d for custom modes)
42      1     cncLevel           0-10
43      1     autoCNCEnabled
44      1     spatialAudioType   0=off, 1=room, 2=head
45      1     windBlockEnabled
46      1     ?
47      1     ancToggleEnabled
```

Preset modes have flags[3]=0x00 (locked); custom/user modes have flags[3]=0x01 (writable).

| Mode  | Name       | Editable | Configured | Notes |
|-------|------------|----------|------------|-------|
| 0     | Quiet      | No       | No         | Firmware preset |
| 1     | Aware      | No       | No         | Firmware preset |
| 2     | Immersion  | No       | No         | Firmware preset |
| 3     | Cinema     | No       | No         | Firmware preset |
| 4     | (custom)   | **Yes**  | Yes        | User-created in app |
| 5-10  | None/Custom| **Yes**  | No         | Empty slots, fully configurable |

##### The real CNC path: `[31.10]` AudioModesSettingsConfig

The ModeConfig-slot approach works for configuring stored profiles, but for
**live** CNC/spatial/wind/ANC control, use `[31.10]` AudioModesSettingsConfig
SETGET directly. This is the same register the Bose app writes to, fully
unauthenticated, and applies immediately without mode switching.

```python
# [31.10] SETGET, 5-byte payload: [cnc, autoCNC, spatial, wind, anc]
# cnc: 0-10 (INVERTED: 0=max ANC, 10=most ambient)
# autoCNC: 0 only (1 is rejected with Runtime error 8)
# spatial: 0=off, 1=room, 2=head
# wind: 0=off, 1=on
# anc: 0=off, 1=on
send(bmap_packet(31, 10, OP_SETGET, [5, 0, 0, 0, 1]))
# ↑ CNC level 5, autoCNC off, spatial off, wind block off, ANC on
```

**Critical audibility interaction:** the CNC level only produces an audible
difference when `anc=on` AND `wind=off`. Wind Block masks the CNC DSP path
(probably to prevent wind compression from fighting ANC). With wind on,
CNC 0 and CNC 10 sound identical.

Also: `autoCNCEnabled=1` causes Runtime error 8 — firmware rejects it.
Only manual CNC (auto_cnc=0) is allowed.

### Mode Config Details (raw data)
```
Mode 0 (Quiet):     000001000001 "Quiet"     ...00000001
Mode 1 (Aware):     0100020000014 "Aware"    ...020a0000000001
Mode 2 (Immersion): 020022000001 "Immersion" ...0002000001
Mode 3 (Cinema):    0300240000004 "Cinema"   ...0001000001
Mode 4 (Home):      04000a010100 "Home"      ...1d000000010001
Mode 5 (None):      0500000100004 "None"     ...1d0a0000010001
```

### Writing — Settings Block SETGET (Also Unauthenticated!)

The SETGET operator on the Settings block [1.x] is ALSO unauthenticated.
SET and START are auth-gated (error 5), but SETGET works.

#### Settings SETGET Summary

All of these use `[1, FUNC, 0x02, LEN, ...PAYLOAD]` (block 1, operator SETGET):

| Func | Name           | Payload (SETGET)              | Notes |
|------|----------------|-------------------------------|-------|
| [1.2]  | ProductName  | UTF-8 string (no flag byte)  | Free-form device name |
| [1.3]  | VoicePrompts | 1 byte: (enabled<<5)\|lang   | See language table below |
| [1.5]  | CNC          | —                            | **AUTH REQUIRED** (use [31.10] instead) |
| [1.7]  | EQ/Range     | 2 bytes: [value, bandId]     | value=-10..+10, band=0/1/2 |
| [1.9]  | Buttons      | 3 bytes: [btnId, evt, mode]  | See button tables below |
| [1.10] | Multipoint   | 1 byte: 0=off, 1=on         | |
| [1.11] | Sidetone     | 2 bytes: [persist, mode]     | mode: 0=off,1=high,2=med,3=low |
| [1.24] | AutoPlayPause| 1 byte: 0=off, 1=on         | Pause on ear removal |
| [1.27] | AutoAnswer   | 1 byte: 0=off, 1=on         | Auto-answer calls |

#### EQ Details [1.7]
```
SETGET payload: [VALUE, BAND_ID]
  - VALUE: -10 to +10 as signed byte (0xf6 to 0x0a)
  - BAND_ID: 0=Bass, 1=Mid, 2=Treble

GET response: 3× 4-byte groups [min, max, current, bandId]
  - e.g. f60a0500 f60a0001 f60af702 = bass=+5, mid=0, treble=-9
```

#### Voice Prompt Languages [1.3]
> **Removed in current firmware.** [1.3] SETGET accepted but the feature is no longer
> exposed by the device UI. Language IDs from earlier firmware (for reference):

| ID | Language | ID | Language |
|----|----------|----|-----------| 
| 0  | UK English | 12 | Hebrew |
| 1  | US English | 13 | Turkish |
| 2  | French     | 14 | Dutch |
| 3  | Italian    | 15 | Japanese |
| 4  | German     | 16 | Cantonese |
| 5  | EU Spanish | 17 | Arabic |
| 6  | MX Spanish | 18 | Swedish |
| 7  | BR Portuguese | 19 | Danish |
| 8  | Mandarin   | 20 | Norwegian |
| 9  | Korean     | 21 | Finnish |
| 10 | Russian    | 22 | Hindi |
| 11 | Polish     | | |

#### Button Remapping [1.9]

##### GET Response Format (7+ bytes)
```
[0]   buttonId          — which physical button
[1]   buttonEventType   — what gesture triggers it
[2]   configuredAction  — current assigned action
[3:7] supportedMask     — bitmask of supported ActionButtonMode values
[7:]  unavailableMask   — bitmask of unavailable modes (optional)
```

##### SETGET Payload (3 bytes)
```
[buttonId, buttonEventType, newActionMode]
```

Note: On QC Ultra 2, actual remapping requires the SET operator (cloud ECDH auth).
SETGET echoes back but does not change the action.

##### Button IDs (ConfigurableButtonId)
| ID   | Name |
|------|------|
| 0x00 | DistalCnc (CNC button) |
| 0x01 | Reserved |
| 0x02 | VPA (voice assistant) |
| 0x03 | RightShortcut |
| 0x04 | LeftShortcut |
| 0x80 | Shortcut (QC Ultra 2 programmable button) |

##### Button Event Types
| ID | Gesture |
|----|---------|
| 0  | Reserved |
| 1  | Rising edge |
| 2  | Falling edge |
| 3  | Short press |
| 4  | Single press |
| 5  | Press and hold |
| 6  | Double press |
| 7  | Double press and hold |
| 8  | Triple press |
| 9  | Long press |
| 10 | Very long press |
| 11 | Very very long press |
| 12 | Very very very long press |

##### Action Button Modes
| ID | Action | ID | Action |
|----|--------|----|--------|
| 0  | NotConfigured | 11 | TrackBack |
| 1  | VPA (voice assistant) | 12 | FetchNotifications |
| 2  | ANC cycle | 13 | WindMode |
| 3  | BatteryLevel | 14 | Disabled |
| 4  | PlayPause | 15 | ClientInteraction |
| 5  | IncreaseCNC | 16 | SpotifyGo |
| 6  | DecreaseCNC | 17 | ModesCarousel |
| 7  | ToggleWakeWord | 19 | SpatialAudioMode |
| 8  | SwitchDevice | 20 | LineInSwitch |
| 9  | ConversationMode | 21 | Linking |
| 10 | TrackForward | | |

##### Current Config (QC Ultra 2)
```
Button 0x80 (Shortcut), long_press → Disabled
Supported actions: SwitchDevice, TrackBack, unknown(22), unknown(25)
Raw: 80090e00094002
```

## What Requires Authentication

SET and START operators on most blocks return error 5 (OpNotSupp) without
completing cloud-mediated ECDH authentication.

Confirmed auth-required:
- Settings.Buttons [1.9] — actual remapping (SETGET echoes but is ignored)
- Settings.StandbyTimer [1.4] — auto-off timer
- AudioModes.DefaultMode [31.4] — both GET and SETGET produce no response
- AudioModes.Favorites [31.8] — writes always Runtime 8; read-only without auth
- Block 13 — all writes auth-gated

## RFCOMM Channels
| Channel | Purpose |
|---------|---------|
| 1 | SPP (connection refused) |
| 2 | **BMAP control** — primary protocol channel |
| 8 | Refused |
| 14 | Status beacon (`ff 55 02 00 ee 10` every ~1s) |
| 22 | Diagnostic push (4 Block-3 packets on connect, then silent) |
| 24 | Silent (accepts connections, sends nothing) |

## USB Interface

The headphones expose USB HID interfaces when connected via USB-C:

| Interface | Class | Device | Purpose |
|-----------|-------|--------|---------|
| 0 | Vendor HID | hidraw9 | BMAP control channel (bidirectional) |
| 1 | Consumer HID | hidraw10 | Media keys (play/pause/volume) |
| 2 | Audio Control | — | USB audio control |
| 3 | Audio Streaming | — | USB audio data (mic, isochronous) |

### USB HID Report IDs (Interface 0, Vendor Specific 0xFF00)

| Report ID | Direction | Size | Likely Purpose |
|-----------|-----------|------|----------------|
| 0x09 | IN | 126 bytes | Small BMAP responses |
| 0x0a | FEATURE | 62 bytes | Feature report (config?) |
| 0x0c | OUT | 1022 bytes | BMAP commands (main) |
| 0x0d | IN | 259 bytes | BMAP responses (main) |
| 0x0e | OUT | 512 bytes | BMAP commands (alt) |
| 0x0f | IN | 512 bytes | BMAP responses (alt) |
| 0x10 | OUT | 675 bytes | BMAP commands (large) |
| 0x11 | IN | 675 bytes | BMAP responses (large) |

Sending BMAP packets in report 0x0c gets `04 01 05` — "not initialized" / not in DFU mode.
The Bose Updater (Windows, Qt/C++) uses this interface for firmware updates via AWS S3.

**Conclusion:** USB is for firmware updates. Use Bluetooth RFCOMM for control.

### USB Device Info
```
Vendor:  0x05a7 (Bose Corporation)
Product: 0x4082 (Bose QC Ultra 2 HP)
Speed:   Full Speed (12 Mbps)
```

## Function Block Map
| Block | Name              | Functions found (readable) | Auth-gated writes |
|-------|-------------------|---------------------------|-------------------|
| 0     | ProductInfo       | 0,1,2,3,5,6,7,12,15,17,23 | 4 |
| 1     | Settings          | 0,2,3,5,7,9,10,11,12,24,27,**34** | 1 |
| 2     | Status            | 0,2,5,16,21 | 1,18 |
| 3     | FirmwareUpdate    | 0,1,4,6,7,15,16 | 2,5,8,9 |
| 4     | DeviceManagement  | 0,1,4,8,9,14,18 | 2,3,5,6,7,15,19 |
| 5     | AudioManagement   | 0,1,3,4,5,7,13,17 | 2,6 |
| 6     | CallManagement    | 0 | 1 |
| 7     | Control           | 0,1,4 | 5,6,11 |
| 8     | Debug             | 7,8 | 3,10,13,16,22 — other funcs: err24 |
| 9     | Notification      | 0,2 | 1 |
| 13    | Unknown           | 0,1,13,14 | 7–12,15 |
| 18    | Authentication    | 0,1,9,11,12,13,24 | 2,19,27,28,29 |
| 22–26 | Unknown (err24)   | — | most funcs |
| 31    | AudioModes        | 0,2,3,6,8,10,11 | 1 |

**Error 24 (0x18):** Blocks 8 (partial), 22–26 return this on most functions.
Likely "not available on this transport" or SecureSession required.
Blocks 32–63 all return FblockNotSupp.

## Settings Functions (Block 1)
| Func | Name             | Read value | Notes |
|------|------------------|------------|-------|
| 0    | FblockInfo       | "1.1.0"    | |
| 2    | ProductName      | "Fargo"    | |
| 3    | VoicePrompts     | 41000081020000 | |
| 5    | CNC              | 0b0003     | [numSteps=11, step=0, flags=3] |
| 7    | RangeControl/EQ  | f60a0000f60a0001f60a0002 | 3-band EQ, all at 0 |
| 9    | Buttons          | 80090e00094002 | |
| 10   | Multipoint       | 07         | |
| 11   | Sidetone         | 01020f     | |
| 12   | SetupComplete    | 01         | |
| 24   | AutoPlayPause    | 01         | |
| 27   | AutoAnswer       | 01         | |
| 34   | Unknown          | 01         | Discovered via GetAll; invisible to direct scan |

## Status Functions (Block 2)
| Func | Name            | Read value | Decoded |
|------|-----------------|------------|---------|
| 2    | BatteryLevel    | 50ffff00   | 0x50 = 80% battery |

## CNC (Noise Cancellation) Details
- Read: GET [1.5] returns `[numSteps, currentStep, flags]`
  - numSteps: total steps (11 on this device = 0-10)
  - currentStep: current level (0=min, 10=max)
  - flags: bit0=isEnabled, bit1=!userEnableDisable
- Write live: SETGET [31.10] with `[step, 0, spatial, wind, anc]` — **no auth required**
- Write stored: SETGET [31.6] with 40-byte ModeConfig payload (custom slots 5-10 only)

## EQ Details
- 3-band equalizer stored in [1.7]
- Format: 3x 4-byte groups `[f60a, VALUE, BAND_INDEX]`
- Values are signed bytes (f7=-9, 00=center, 0a=+10)
- Band 0=Bass, 1=Mid, 2=Treble

## Authentication System (Block 18)

### Overview
Cloud-mediated ECDH P-384 challenge-response. The headphones require a signature
from Bose's cloud servers (`nadc.data.api.bose.io`) before granting SET privileges.
The app acts as a proxy between headphones and cloud.

### Auth Capabilities (from [18.1] bitmask `0339083e07`)
Supported: FblockInfo, GetAll, CondensedChallenge, OtpKeyType, ProductName,
PlatformName, ValidatedDeviceIdentityKeypair, PropagateProductIrk,
NoTokenChallenge, ProductToCloudChallenge, ProductToCloudChallengeVerifyResponse,
CloudToProductChallenge, GoogleFeatureKeys, GoogleFeatureKeyData

### Auth Device Info
- Device ECDH public key at [18.9]: P-384 PEM format
- Product name [18.12]: "wolverine"
- Platform [18.13]: "OTG-QCC-384"
- OTP key type [18.11]: 3
- Product IRK [18.24]: `3713b952XXXXXXXXXXXXXXXXXXXXXXXX` (from BMAP response, starts with 0x37 0x13)

### BLE IRK (from Android DataStore, confirmed current)
Stored in `persisted_local_products_discovery` as `unencryptedIrk`:
```
Base64: 3oiv/MY74THZaiPkEfjnvw==
Hex:    de 88 af fc c6 3b e1 31 d9 6a 23 e4 11 f8 e7 bf
```
Timestamp: `2026-07-21T09:20:13.3980`
This IRK resolves the device's BLE Resolvable Private Addresses (RPAs).
Note: This IRK differs from the [18.24] BMAP IRK — likely two different keys
(one for BLE privacy, one for BMAP auth).

### [18.247] SET — Unknown notification
Observed once in frida_out3.txt after auth:
```
[BMAP RX] [18.247] SET : 0b 00 00 00 c8 46 a2 34 00 00 00 00 c4 aa 91 ed 09 00 00 00 11 4a 44 fc 00 00 00
```
27 bytes, fn=247 (0xF7) on Block 18. Likely a timestamp or session nonce pushed by device.
Bytes 4-7 (`c8 46 a2 34`) = 0x34a246c8 = 882050760 — plausible Unix timestamp (2049-03-09?).
Bytes 12-15 (`c4 aa 91 ed`) = 0xed91aac4 = 3985137348.

### Cloud API
- Primary API: `nadc.data.api.bose.io` (QUIC/HTTP3, falls back to HTTPS)
- Identity: `id.api.bose.io/id-idp-mgr-core/`
- Services: `services.api.bose.io` (Apigee gateway)
- Firmware: `ota.cdn.bose.io`
- API key system called "Galapagos"
- App has certificate pinning

### Cloud Auth Service
- Directory endpoint: `https://id.api.bose.io/` (service directory captured at startup)
- Product access URL: `https://id.api.bose.io/id-product-access-core`
- Product JWT URL: `https://id.api.bose.io/id-product-jwt-core`
- Device GUID: `55ec9cc9-e62d-dead-de48-d797a64cfcdb` (maps to fkO4zD6WH3 token)

### Observed Auth Flows Summary
| Flow | Trigger | BMAP packets | Cloud call? |
|------|---------|-------------|-------------|
| Initial pair | First-ever connect | fn=21 SETGET (17B challenge) → fn=76 RESULT (62B) | Yes: `id-product-access-core` |
| Reconnect A | Normal reconnect | fn=13 RESULT (192B) | No |
| Reconnect B | After app restart | fn=15 STATUS (148B) | No |
| Onboarding | Fresh install/pair | fn=31 ERROR + setup complete flow | Unclear |

> Captured credentials, Frida crypto analysis, and next-steps research → see NOTES_private.md

## Block 13 — Undocumented Block

| Func | Access | Value | Notes |
|------|--------|-------|-------|
| 0  | GET  | "1.1.0"       | FblockInfo |
| 1  | GET  | `ff 83`       | -125 as int16; static |
| 7–12 | auth | —           | OpNotSupp without auth |
| 13 | GET  | `01`          | Stable |
| 14 | GET  | `00` or `02`  | Volatile between sessions |
| 15 | auth | —             | OpNotSupp without auth |

- All writes auth-gated
- `[13.1]` GetAll also auth-gated (OpNotSupp)
- `[13.14]` hypothesis: A2DP connection count (0=no stream, 2=two sources via multipoint)

## Hidden Functions (discovered via GetAll)

Direct GET scan misses functions at high indices. `[block.1]` START (GetAll) reveals all.

- **`[1.34]`** GET = `01` — invisible to direct scan; meaning unknown (feature flag? FW 8.2.x?)
- **`[31.11]`** = `1f ff ff ff ff` — `0x1f` = bits 0–4 = modes 0–4 are preset/locked
- **`[31.2]`** = `04 07 00 00 00 7f 02` — current mode index + mode count + unknown
- **`[31.8]`** Favorites = `0b 00 13` — encoding unclear; out of 0–10 mode range

