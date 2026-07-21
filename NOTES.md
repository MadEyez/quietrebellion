# Bose QC Ultra 2 — Reverse Engineering Notes

## TL;DR

**We can control the headphones from Linux without the Bose app, Bose account, or cloud.**

The AudioModes function block (block 31) accepts the `START` operator without
authentication. This lets us switch ANC modes, read battery, read all device info,
and more — all over a direct Bluetooth RFCOMM connection.

Bose locked down SET/SETGET operators behind cloud-mediated ECDH authentication,
but left the START operator on AudioModes wide open. This is the same operator
the app uses to change modes in real time.

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
| [18.19]  | ValidatedDeviceIdentityKeypair | PROCESSING | Accepts public key for auth flow |

#### AudioModes SETGET — Full Config Control (No Auth!)

**BREAKTHROUGH #2:** SETGET on AudioModes block 31 is completely unauthenticated.
Preset modes (0-4) are firmware-locked (return Runtime error 8), but custom mode
slots (5-10) accept full configuration changes via SETGET. Combined with
CurrentMode START to switch to the custom mode, this gives us **complete control
over CNC level, spatial audio, wind block, and ANC** — all without any auth.

| Function | Operator | Status | Notes |
|----------|----------|--------|-------|
| [31.6] ModeConfig | SETGET | **WORKS on modes 5-10** | Full config: CNC, spatial, wind, ANC |
| [31.6] ModeConfig | SET | Error 5 (auth) | SET is auth-gated, but SETGET isn't |
| [31.8] Favorites | SETGET | Echo only (auth-blocked for writes) | Read-only without auth; only echoes existing value |
| [31.8] Favorites | SET | Error 5 (auth) | |
| [31.4] DefaultMode | SETGET | No response (blocked) | Both GET and SETGET timeout; fully auth-gated |
| [31.4] DefaultMode | SET | Error 5 (auth) | |
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
| 4     | Home       | **Yes**  | Yes        | User-created in app |
| 5-10  | None/Custom| **Yes**  | No         | Empty slots, fully configurable |

Mode 4 (Home) is editable because it was created by the user via the app.
Modes 5-10 are empty slots that accept full configuration.
The cloud auth the app uses is likely for syncing profiles across devices,
not for writing to the headphone firmware — SETGET bypasses it entirely.

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
Mode 6 (None):      0600000100004 "None"     ...1d0a0000010001
Mode 7 (None):      0700000100004 "None"     ...1d0a0000010001
```

### Writing — Settings Block SETGET (Also Unauthenticated!)

**BREAKTHROUGH #3:** The SETGET operator on the Settings block [1.x] is ALSO
unauthenticated! SET and START are auth-gated (error 5), but SETGET bypasses auth.

#### Settings SETGET Summary

All of these use `[1, FUNC, 0x02, LEN, ...PAYLOAD]` (block 1, operator SETGET):

| Func | Name           | Payload (SETGET)              | Notes |
|------|----------------|-------------------------------|-------|
| [1.2]  | ProductName  | UTF-8 string (no flag byte)  | Free-form device name |
| [1.3]  | VoicePrompts | 1 byte: (enabled<<5)\|lang   | See language table below |
| [1.5]  | CNC          | —                            | **AUTH REQUIRED** (use [31.6] instead) |
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

**Note:** Button remapping via SETGET is **silently rejected** on QC Ultra 2 —
SETGET returns a STATUS (echo) but the action does not change. Tested: setting
SwitchDevice(8) and ModesCarousel(17) both echo back as Disabled(14). Actual
remapping requires the SET operator (cloud ECDH auth, not implemented).
`bose raw "01 09 02 03 80 09 08"` sends the SETGET packet but will not change the button.

## What Requires Authentication (Blocked)

SET and START operators on most blocks return error 5 (OpNotSupp) without
completing cloud-mediated ECDH authentication. **However, SETGET is often
unauthenticated** — Bose only gated SET and START.

Confirmed auth-blocked (SET/START only):
- Settings.ProductName [1.2] — device name (SET blocked, SETGET untested)
- Settings.Multipoint [1.10] — multipoint toggle
- Settings.StandbyTimer [1.4] — auto-off timer
- **Settings.Buttons [1.9]** — SETGET is accepted (STATUS echoed back) but action does **not** change; actual remapping requires SET (cloud auth). Read-only in practice.
- **AudioModes.DefaultMode [31.4]** — both GET and SETGET produce no response (timeout). Do not call GET; SETGET also blocked.

Confirmed SETGET works without auth:
- Settings.EQ [1.7] — **full equalizer control** (-10 to +10, 3 bands)
- AudioModes.ModeConfig [31.6] — **CNC, spatial, wind, ANC** on custom modes 5–10
- AudioModes.Favorites [31.8] — read-only (echoes current value; writes are Runtime error 8)

## RFCOMM Channels
| Channel | Purpose |
|---------|---------|
| 1 | SPP (connection refused) |
| 2 | **BMAP control** — primary protocol channel |
| 8 | Refused |
| 14 | Status beacon (sends `ff5502...` periodically) |
| 22 | Diagnostic/log stream (not BMAP, dumps data regardless of input) |
| 24 | Silent (purpose unknown) |

## USB Interface

The headphones expose two USB HID interfaces when connected via USB-C:

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

### USB Status

Sending BMAP packets in report 0x0c gets a response on 0x0d, but always the
same 3 bytes: `04 01 05`. This appears to be a "not initialized" error — the
USB BMAP channel likely requires a handshake sequence before accepting commands.

Over Bluetooth RFCOMM, BMAP is immediately active. The USB HID channel
appears to be **firmware update only**, not general BMAP control:

- The Bose Updater (Windows, Qt/C++ app) uses a modified HIDAPI library
  to communicate over USB HID, downloads firmware from AWS S3, and pushes
  it through HID reports
- The `04 01 05` response to all our BMAP attempts is likely "not in DFU mode"
- The updater probably sends a special command to enter firmware update mode
- No desktop Bose app exists for general headphone control — only the updater

**Conclusion:** USB is for firmware updates. Use Bluetooth RFCOMM for control.

### USB Device Info
```
Vendor:  0x05a7 (Bose Corporation)
Product: 0x4082 (Bose QC Ultra 2 HP)
Speed:   Full Speed (12 Mbps)
Serial:  T5333020XXXXXXXXXXXXX
```

## Function Block Map
| Block | Name              | Functions found (readable) | Auth-gated writes |
|-------|-------------------|---------------------------|-------------------|
| 0     | ProductInfo       | 0,1,2,3,5,6,7,12,15,17,23 | 4 |
| 1     | Settings          | 0,2,3,5,7,9,10,11,12,24,27,**34** | 1 |
| 2     | Status            | 0,2,5,16,21 | 1,18 |
| 3     | FirmwareUpdate    | 0,1,4,6,7,15,16 | 2,5,8,9 |
| 4     | DeviceManagement  | 0,1,4,8,9,14,18 | 2,3,5(needs MAC),6(needs MAC),7,15(needs payload),19 |
| 5     | AudioManagement   | 0,1,3,4,5,7,13,17 | 2,6 |
| 6     | CallManagement    | 0 | 1 |
| 7     | Control           | 0,1,4 | 5,6,11 |
| 8     | Debug             | 7,8 | 3,10,13,16,22 — other funcs: err24 |
| 9     | Notification      | 0,2 | 1 |
| 13    | **UNKNOWN**       | 0,1,13,14 | 7,8,9,10,11,12,15 |
| 18    | Authentication    | 0,1,9,11,12,13,24 | 2,19,27,28,29 |
| 22–26 | UNKNOWN (err24)   | 24.13 | most funcs |
| 31    | AudioModes        | 0,2,3,6,8,10,11 | 1 |

**Error 24 (0x18):** Blocks 8 (partial), 22–26 return this unknown error on most functions.
Not to be confused with error 20 (InsecureTransport). Likely means "not available on this transport"
or a Bose-specific "SecureSession required" variant. Blocks 32–63 all return FblockNotSupp.

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

## Status Functions (Block 2)
| Func | Name            | Read value | Decoded |
|------|-----------------|------------|---------|
| 2    | BatteryLevel    | 50ffff00   | 0x50 = 80% battery |

## CNC (Noise Cancellation) Details
- Read: GET [1.5] returns `[numSteps, currentStep, flags]`
  - numSteps: total steps (11 on this device = 0-10)
  - currentStep: current level (0=min, 10=max)
  - flags: bit0=isEnabled, bit1=!userEnableDisable
- Write: SetGet [1.5] with payload `[step, enabled?1:0]`
  - **Requires authentication** — returns OpNotSupp error 5 without auth

## EQ Details
- 3-band equalizer stored in [1.7]
- Format: 3x 4-byte groups `[f60a, VALUE, BAND_INDEX]`
- Values are signed bytes (f7=-9, 00=center, 0a=+10)
- Band 0=Bass, 1=Mid, 2=Treble

## Authentication System (Block 18)

### Overview
Cloud-mediated ECDH P-384 challenge-response. The headphones require a signature
from Bose's cloud servers (`nadc.data.api.bose.io`) before granting SET/SETGET
privileges. The app acts as a proxy between headphones and cloud.

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
- Product IRK [18.24]: `3713b952XXXXXXXXXXXXXXXXXXXXXXXX`

### Auth-Challenge Passive Drain (2026-07-21)

`[18.19]` START with a real P-384 ephemeral public key:
- Immediate: PROCESSING (key accepted)
- ~5 seconds later: RESULT (empty payload) — no challenge bytes delivered
- `[18.27]`–`[18.29]` remain OpNotSupp even after key exchange

**Conclusion:** `[18.19]` is only a key-registration step, not a challenge-delivery mechanism.
The challenge flows directly between the device and Bose's cloud service — it never passes
through our RFCOMM connection. No offline bypass is possible over RFCOMM alone.

### Auth Flow (confirmed)
1. App generates an ephemeral ECDH P-384 keypair
2. `[18.19]` START → app sends its public key → device replies PROCESSING, then RESULT (empty, ~5s)
3. Device contacts cloud (`nadc.data.api.bose.io`) directly to exchange the challenge
4. Cloud signs the challenge and returns a response
5. `[18.28]` → app forwards cloud response to the device
6. `[18.29]` → cloud-to-product verification step
7. Device grants SET/SETGET privileges for this session

### Cloud API
- Primary API: `nadc.data.api.bose.io` (uses QUIC/HTTP3, falls back to HTTPS)
- Identity: `id.api.bose.io/id-idp-mgr-core/`
- Services: `services.api.bose.io` (Apigee gateway)
- Config: `nadc-config.data.api.bose.io`
- Firmware: `ota.cdn.bose.io`, `updates-framingham-prod.smartproducts.bose.io`
- API key system called "Galapagos" — key fetched from remote config
- App has certificate pinning — rejects user-installed CA certs

### Why Auth Bypass Works
Bose protected SET (operator 0) and SET_GET (operator 2) behind cloud auth.
But START (operator 5) on the AudioModes block was left unprotected. The app
uses START to change modes in real time (it's the "instant switch" path).
SET_GET is used for persistent config changes. This distinction means we can
control the headphones in real time but can't change saved configuration.

## Audio Source & Device Routing

### AudioManagement Source [5.1] — Query Active Source
GET-only function. Response payload:
```
[0-1]  Supported sources (bitset, 2 bytes)
[2]    Active source type: 0=NONE, 1=BLUETOOTH, 2=AUXILIARY
[3+]   Source-specific data (BLUETOOTH: 6 bytes MAC address)
```

Source types from `AudioControlSourceType.java`:
- NONE (0x00) — no active source
- BLUETOOTH (0x01) — BT A2DP, 6 bytes additional data (MAC)
- AUXILIARY (0x02) — 3.5mm line-in, no additional data

**No SET/START on [5.1]** — source switching happens implicitly (plug in aux,
or route a BT device via [4.12]).

### DeviceManagement Routing [4.12] — Switch Active BT Device
START operator to route audio to a specific paired BT device (multipoint switch).

Payload (7 bytes):
```
[0]    Flags: 0x82 (bit7=UP routing direction, bit1=device slot)
[1-6]  Target device MAC address (6 bytes)
```

Response handling (from `DeviceManagementBmapPacketParser.java`):
- **RESULT**: bytes [1-6] = MAC of now-active device (success)
- **ERROR**: bytes [0-1] = 16-bit error code
- **STATUS**: bytes [2-7] = MAC of newly routed device

ROUTING_TYPE enum: UP=1 (value << 7 = 0x80), DOWN=0.

### AudioManagement Control [5.3] — Transport Controls
START operator with single-byte payload. Values from `AudioControlValue.java`:
```
0x00  STOP          (not confirmed working)
0x01  PLAY          ✅ confirmed (PROCESSING)
0x02  PAUSE         ✅ confirmed (RESULT)
0x03  TRACK_FORWARD ✅ confirmed (PROCESSING)
0x04  TRACK_BACK    ✅ confirmed (PROCESSING)
0x05  FAST_FORWARD_PRESS   (ERROR — not available on QC Ultra 2)
0x06  FAST_FORWARD_RELEASE (ERROR)
0x07  REWIND_PRESS         (ERROR)
0x08  REWIND_RELEASE       (ERROR)
```

**Requirement:** An active A2DP audio stream must be present. Without a stream,
all commands return ERROR 06 (InvalidData). The capabilities bitmask from GET
`[1f]` = bits 0–4 set = 5 supported operations (0x01–0x05).

### AudioManagement MediaState [5.4]
GET returns 3 bytes: `[state, ?, ?]`
- `state = 0x01` → Playing
- `state = 0x02` → Paused
- `state = 0x00` → Stopped

### AudioManagement MediaProgress [5.7]
GET returns 6 bytes: `[00 00, dur_hi, dur_lo, pos_hi, pos_lo]`
- `bytes[2-3]` = track duration in seconds (big-endian)
- `bytes[4-5]` = current playback position in seconds (big-endian, increases while playing)

### DeviceManagement Functions (Block 4) — Full Map
From `DeviceManagementPackets.java` + live probing:
| Func | Name | Notes |
|------|------|-------|
| 0 | FblockInfo | |
| 1 | Connect | GET: `[00 00 03]` (static, likely capacity info). START: InvalidData with all payloads tested. |
| 2 | Disconnect | |
| 3 | RemoveDevice | |
| 4 | ListDevices | Returns paired device list (MACs only) |
| 5 | Info | Device name lookup: GET(mac) → `[mac(6), unk, unk, 0x03, name...]` |
| 7 | ClearDeviceList | |
| 8 | PairingMode | GET: `[00]`=not pairing. START `[0x01]`=enter pairing mode |
| 9 | LocalMacAddress | GET: returns headphone's own BT MAC (6 bytes) |
| 10 | PrepareP2P | |
| 11 | P2PMode | |
| 12 | Routing | Switch active multipoint device (only when 2 A2DP connections active) |
| 14 | Unknown | GET: `[01]` — static, does not change with connection count |
| 18 | Unknown | GET: `[01]` — static, does not change with connection count |

## BMAP Error Codes
| Code | Name             | Description |
|------|------------------|-------------|
| 0    | Unknown          | Unknown error |
| 1    | Length           | Invalid length |
| 2    | Chksum           | Invalid checksum |
| 3    | FblockNotSupp    | Function block not supported |
| 4    | FuncNotSupp      | Function not supported |
| 5    | OpNotSupp        | Operator not supported (needs auth) |
| 6    | InvalidData      | Data values incorrect |
| 7    | DataUnavailable  | Requested data not available |
| 8    | Runtime          | Temporary read/write failure |
| 9    | Timeout          | Timeout |
| 10   | InvalidState     | Not applicable to current state |
| 20   | InsecureTransport| Packet on insecure transport |

## BMAP Protocol Class References
- `com.bose.bmap.messages.enums.spec.BmapFunctionBlock` — block IDs
- `com.bose.bmap.messages.enums.spec.BmapFunction` — function IDs
- `com.bose.bmap.messages.enums.spec.BmapOperator` — operator IDs
- `com.bose.bmap.messages.packets.AudioModesCurrentModeStartPacket` — mode switch
- `com.bose.bmap.messages.packets.SettingsCncSetGetPacket` — CNC control
- `com.bose.bmap.service.SppConnectionManager` — SPP UUID: 00001101-...
- `com.bose.bmap.messages.models.settings.CncLevel` — CNC response parser
- `com.bose.bmap.messages.responses.SettingsCncResponse` — CNC payload format
- `com.bose.bmap.utils.encryption.ECDH` — secp256r1 key generation
- `com.bose.bmap.model.factories.AuthenticationPackets` — auth error codes & capabilities bitmask

## Tools
- `bmap-capture.py` — Interactive setting change capture tool
- `captures/` — Captured setting toggle data (8 captures)
- `snoop/` — Network captures and bugreports

## Quick Reference: Control Headphones from Linux

```python
import socket

BOSE_MAC = "68:F2:1F:XX:XX:XX"
sock = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM)
sock.settimeout(2)
sock.connect((BOSE_MAC, 2))

# Switch to Quiet (full ANC): mode=0
sock.send(bytes([31, 3, 0x05, 2, 0, 0]))

# Switch to Aware (transparency): mode=1
sock.send(bytes([31, 3, 0x05, 2, 1, 0]))

# Switch to Immersion: mode=2
sock.send(bytes([31, 3, 0x05, 2, 2, 0]))

# Read battery level
sock.send(bytes([2, 2, 0x01, 0x00]))
resp = sock.recv(4096)
battery_pct = resp[4]  # hex value, e.g. 0x50 = 80%

# Read current mode
sock.send(bytes([31, 3, 0x01, 0x00]))
resp = sock.recv(4096)
current_mode = resp[4]  # 0=Quiet, 1=Aware, 2=Immersion, etc.
```

## QC35 NoiseCancellation Block (Block 3) — Firmware 4.8.1

Block 3 is a separate NoiseCancellation fblock on the QC35, distinct from the
Settings-level ANR control at [1.6]. Investigation results:

### Functions
| Func | GET | SET/SETGET | START | Notes |
|------|-----|-----------|-------|-------|
| 3.1  | `01` or `02` | Auth-gated (error 5) | Auth-gated | Binary NC system state |
| 3.2  | Auth-gated | Auth-gated | **14-byte payload accepted** | NC state transition |
| 3.3  | Auth-gated | — | — | Unknown |
| 3.4  | `01000000020000000000` (10 bytes) | Auth-gated | — | NC config/capabilities |
| 3.5  | Auth-gated | — | — | Unknown |
| 3.6  | Empty STATUS | — | — | Unknown |
| 3.7  | Auth-gated | — | — | Unknown |
| 3.8+ | FuncNotSupp | — | — | Not implemented |

### [3.2] START Payload Format (14 bytes)
```
Offset  Size  Field
0       1     currentState  — must match [3.1] value
1-3     3     reserved (zeros)
4       1     targetState   — must be a valid transition target
5-13    9     reserved (zeros)
```

Accepts exactly 14-17 bytes (Length error outside). Only valid transition found:
`01 → 02` (RESULT returned, [3.1] changes). All others return error 15 or InvalidData.

### [3.1] State Values
- `01` = default state (after ANR changes, after power cycle)
- `02` = alternate state (reached via [3.2] START transition)

### Key Findings
- [3.1] does **not** correlate with ANR mode — changing ANR via [1.6] does not affect [3.1]
- [3.4] config is static (`01 00 00 00 02 00 00 00 00 00`) regardless of ANR mode
- The [3.2] START is a **binary state toggle**, not continuous NC level control
- This is likely the low-level NC hardware enable/disable — not useful for user-facing control
- **ANR at [1.6] remains the correct interface** for NC mode control on QC35 firmware 4.8.1
- The continuous CNC slider (0-10) seen on older firmware (1.x) is not recoverable via block 3

### Conclusion
Block 3 on QC35 firmware 4.8.1 is a low-level NC system control that was likely
exposed on older firmware but is now mostly auth-gated. The only unauthenticated
path ([3.2] START) is a binary state transition with no practical NC level control.
The discrete ANR modes (off/high/wind/low) via [1.6] SETGET are the full extent
of unauthenticated NC control on this firmware version.

## Future Work / Status

### Completed ✅
- ~~Crack ModeConfig SETGET payload format~~ — full CNC/spatial/wind/ANC control
- ~~Crack Settings SETGET~~ — EQ, name, sidetone, multipoint, auto-pause, auto-answer
- ~~Button remapping [1.9]~~ — not possible without auth (SETGET silently ignored)
- ~~USB-C connection~~ — HID interface found; firmware-update only, needs DFU handshake
- ~~Audio source/device routing~~ — [5.1] source query, [4.12] routing, [5.3] transport controls
- ~~Transport controls [5.3]~~ — play/pause/next/prev confirmed with active A2DP stream
- ~~MediaState/MediaProgress~~ — [5.4] state, [5.7] position + duration
- ~~Full block sweep 0–63~~ — Block 13 discovered; blocks 32–63 all FblockNotSupp
- ~~Channel 14/22 decode~~ — beacon + diagnostic push decoded
- ~~Auth challenge format~~ — fully resolved; see Auth section
- ~~Passive notification listener~~ — resolved: no push on unauthenticated RFCOMM
- ~~[31.4] DefaultMode SETGET~~ — no response; both GET and SETGET are blocked
- ~~Block 13 GetAll~~ — [13.1] START → OpNotSupp; no hidden functions accessible
- ~~[31.8] Favorites SETGET~~ — Runtime 8 for all values except original echo; auth-blocked

### Auth boundary: fully mapped (2026-07-21)
`[18.19]` START + P-384 public key → PROCESSING (immediate) → RESULT (empty, ~5s later).
No challenge payload is delivered over RFCOMM. `[18.27]`–`[18.29]` remain OpNotSupp.
**Auth is fully cloud-dependent. No offline bypass is possible over RFCOMM alone.**
The only remaining path would be a cloud MITM via Frida + certificate unpinning on Android.

### Low priority — require physical interaction
- **[13.14] correlation** — put headphones on/off while polling; may confirm wear-detection hypothesis
- **[1.34] decode** — single byte `01`; toggle remaining app settings (low-latency, hearing protection?)
- **Channel 14 payload** — observe `ee 10` while charging to see if it tracks battery state

### Dead ends (do not pursue further)
- [31.4] DefaultMode — both SETGET and GET produce no response; fully auth-gated
- Blocks 22–26 (err24) — likely SecureSession or USB-only transport requirement
- [31.8] Favorites — writes always Runtime 8; read-only without auth
- Block 13 GetAll / writes — all auth-gated
- Passive notifications — confirmed absent on unauthenticated RFCOMM
- [5.17] / [4.6] extra bytes / [7.1] — static fields with no actionable path
- USB BMAP — Bose Updater / DFU mode only; not a general control channel

## RFCOMM Channel Details (Updated 2026-07-21)

### Channel 14 — Status Beacon
Sends exactly 6 bytes every ~1 second: `ff 55 02 00 ee 10`
- `ff 55` = magic header (Bose proprietary framing, NOT BMAP)
- `02` = message type or payload length = 2
- `00` = ??? 
- `ee 10` = payload: meaning unknown, static during session

**Open question:** capture while charging/discharging to see if `ee 10` changes with battery state.

### Channel 22 — Diagnostic Push
Pushes 4 BMAP-framed packets from **Block 3 (FirmwareUpdate)** immediately on connect, then goes silent:

| Packet | Raw | Notes |
|--------|-----|-------|
| `[3.1]` SET | `d7 38 53` | 3 bytes, meaning unknown |
| `[3.2]` SET | `69 e6 60 df 2c 14` | 6 bytes — possible internal MAC or build timestamp + suffix |
| `[3.3]` SET | `ff ff ff` | All-ones — error/unavailable indicator |
| `[3.10]` SET | `4c 3a f8 7f 24 62 bf 8f` | 8 bytes — 64-bit counter or hash |

These arrive as SET operator (0) **from** the device — the device is pushing diagnostic state, not responding to GET.

### Channel 24 — Silent
Accepts connections, sends nothing.

## Block 13 — NEW (Unknown, discovered 2026-07-21)

| Func | Access | Value | Notes |
|------|--------|-------|-------|
| 0 | GET | "1.1.0" | FblockInfo |
| 1 | GET | `ff 83` | 16-bit value; -125 as int16; static |
| 7–12 | auth | — | OpNotSupp without auth |
| 13 | GET | `01` | Single byte, stable |
| 14 | GET | `00` or `02` | **Volatile between sessions** |
| 15 | auth | — | OpNotSupp without auth |

All write operators (SETGET/START) are auth-gated. Block 13 is read-only without cloud auth.

**[13.14] hypothesis:** Tracks A2DP connection count (2 = two audio sources active via multipoint; 0 = no audio stream). Needs correlation test.

## New Function Details (2026-07-21 Sweep)

### [4.6] — Connection Info by MAC (NEW)
`GET [6-byte MAC]` → `[MAC (6B)] [4B extra]`
```
GET [f0:57:a6:07:14:61 (PC)] → f0 57 a6 07 14 61 1f 0f 9c e9
  [6]   = 0x1f = 31 — LQ? caps? same value as [5.3] caps — coincidence
  [7]   = 0x0f = 15
  [8-9] = 0x9ce9 — 16-bit: RSSI pair? connection handles?
```

### [5.13] — MediaProgress Extended Format
`[pos_hi, pos_lo, 00, 00, dur_hi, dur_lo, 00, 00, dur_hi, dur_lo]` (10 bytes)
- [0-1]: position in seconds (uint16 BE) — matches [5.7][5]
- [4-5]: track duration (matches [5.7][2-3])
- [8-9]: track duration repeated (buffer end? playlist end?)

### [5.17] — Unknown, 80 bytes, STATIC
Identical across 3 consecutive reads. Not playback-related. Likely audio DSP filter bank or
routing coefficients. Repeating `7f ff ff ff` (INT32_MAX) = unused slots.
Notable constants: `fe 08` (−504), `ae fc` (−20740), `7f 46 dc 5c`.

### [8.7] — BMAP Session Counter
Increments with each new RFCOMM ch2 connection since power-on.
Stable within a session. Observed: 03 → 04 across successive sessions.

### [3.4] — Backup Firmware Slot Version
`00 00 00 00 00 "0.0.0"` — "0.0.0" = backup slot empty (no previous OTA image stored)

### [3.6] — Active Firmware Slot
`00 01` = slot 1 active (dual-bank OTA layout; slot 0 = backup, slot 1 = running)

### [7.1] = `08 73` = 2163
Meaning unknown. Candidates: session uptime in some unit, battery cell voltage (unlikely),
or control-state bitfield `0x0873`.

## Notification System (Block 9)

### `[9.2]` — Notification Subscription Bitmask
`GET` → `00 00 00 00` (not subscribed)
`SETGET [00 00 00 01]` → `00 00 00 01` (subscribed)

Bit 0 = enable push notifications. Other values (`ff ff ff ff`, `01 00 00 00`) are clamped to 0
(invalid), only little-endian `00 00 00 01` is accepted.

**Critical finding:** Subscribing via `[9.2]` does NOT trigger unsolicited STATUS packets over
unauthenticated RFCOMM. Tested: 30s passive drain after subscription + [1.1] GetAll = 0 packets.
Conclusion: Push notifications are only delivered to **authenticated** BMAP sessions (or BLE GATT).
The polling approach (`bmap-capture.py` style) is the correct method for observing state changes
over unauthenticated RFCOMM.

## AudioModes — Complete Mode List (GetAll [31.1] START)

GetAll reveals all 11 mode configs + favorites + current settings in one shot.
Mode 4 is the user's custom mode, renamed from "Home" to **"Arbeit"** (German for "Work").

### Current Mode Config (from GetAll, 2026-07-21)
| Mode | Name | VP | Editable | CNC | spatial | wind | ANC |
|------|------|----|----------|-----|---------|------|-----|
| 0 | Quiet | 0x0001 | No | 0 | off | off | on |
| 1 | Aware | 0x0002 | No | 10 | off | wind | on |
| 2 | Immersion | 0x0022 | No | 0 | room | off | on |
| 3 | Cinema | 0x0024 | No | 0 | room | off | on |
| 4 | **Arbeit** | 0x000b | **Yes** | **5** | off | off | **on** |
| 5–10 | None | 0x0000 | Yes | 10 | off | off | on |

### `[31.8]` Favorites = `0b 00 13`
Raw 3-byte response. Encoding unclear — `0b`=11, `00`=0, `13`=19 are out of the 0-10 mode range.
Possibly: `[0b 00]` = 2-byte slot descriptor + `13` = something else. Needs correlation with app.

### `[31.10]` Current Live Settings = `05 00 00 00 01`
Format: `[cnc, autoCNC, spatial, wind, anc]`
- CNC=5, autoCNC=off, spatial=off, wind=off, ANC=on

### `[31.11]` = `1f ff ff ff ff` (NEW — 5 bytes)
- `1f` = 0b00011111 = bits 0–4 set = modes 0–4 are preset/locked
- `ff ff ff ff` = possibly: all custom slots (5-10+) are writable, or some capability bitmask
Hypothesis: supported/locked mode mask — first 5 bits = 5 factory presets, rest = available for customization.

### `[31.2]` = `04 07 00 00 00 7f 02` (7 bytes)
Meaning unknown. `04` = current mode index (Arbeit=4 was active). `07` = count of modes in carousel?
`7f 02` = some 16-bit value.

## Settings — Function 34 (NEW)

`[1.34]` GET = `01` (single byte, boolean or enum).
Discovered via GetAll `[1.1]` START — invisible to direct GET scan (was beyond FUNC_MAX=25).
Meaning unknown. Candidates: low-latency mode, hearing protection, conversation mode auto-on,
or a feature flag added in firmware 8.2.x.

