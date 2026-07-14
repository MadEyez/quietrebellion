/*
 * BoseConnection.kt – High-level BMAP operations for QC Ultra (wolverine).
 * Port of BoseConnection.cs / python/pybmap/connection.py.
 */
package net.quietrebellion

import android.util.Log
import java.io.Closeable

private const val TAG = "BoseCtl"

class BoseProtocolException(message: String) : Exception(message)

class BoseConnection(private val transport: BluetoothTransport) : Closeable {

    // ── Read operations ───────────────────────────────────────────────────────

    suspend fun battery(): Int {
        val (fb, fn) = QcUltra2.Battery
        return QcUltra2.parseBattery(get(fb, fn).payload)
    }

    /** Returns true if charging, false if not, null if unavailable. */
    suspend fun chargingState(): Boolean? = try {
        val (fb, fn) = QcUltra2.ChargingState
        val p = get(fb, fn).payload
        if (p.isNotEmpty()) p[0] != 0.toByte() else null
    } catch (_: Exception) { null }

    /** Returns set of favorited mode indices and the total mode count. */
    suspend fun favorites(): Pair<Set<Int>, Int> {
        val (fb, fn) = QcUltra2.Favorites
        val p = get(fb, fn).payload
        return QcUltra2.parseFavorites(p) to (if (p.isNotEmpty()) p[0].toInt() and 0xFF else 11)
    }

    suspend fun firmware(): String {
        val (fb, fn) = QcUltra2.Firmware
        return QcUltra2.parseFirmware(get(fb, fn).payload)
    }

    suspend fun deviceName(): String {
        val (fb, fn) = QcUltra2.ProductName
        return QcUltra2.parseProductName(get(fb, fn).payload)
    }

    suspend fun modeIndex(): Int {
        val (fb, fn) = QcUltra2.CurrentMode
        val p = get(fb, fn).payload
        return if (p.isNotEmpty()) p[0].toInt() and 0xFF else -1
    }

    suspend fun audioSettings(): AudioSettings {
        val (fb, fn) = QcUltra2.AudioSettings
        return QcUltra2.parseAudioSettings(get(fb, fn).payload)
    }

    suspend fun eq(): List<EqBand> {
        val (fb, fn) = QcUltra2.Eq
        return QcUltra2.parseEq(get(fb, fn).payload)
    }

    suspend fun sidetone(): Int {
        val (fb, fn) = QcUltra2.Sidetone
        return QcUltra2.parseSidetone(get(fb, fn).payload)
    }

    suspend fun multipoint(): Boolean {
        val (fb, fn) = QcUltra2.Multipoint
        return QcUltra2.parseMultipoint(get(fb, fn).payload)
    }

    suspend fun source(): Pair<String, String?> {
        val (fb, fn) = QcUltra2.Source
        return QcUltra2.parseSource(get(fb, fn).payload)
    }

    suspend fun pairedDeviceMacs(): List<String> {
        val (fb, fn) = QcUltra2.PairedDevices
        return QcUltra2.parsePairedDevices(get(fb, fn).payload)
    }

    /** Returns dict of mode index → display name (preset 0-3 + custom 4-10). */
    suspend fun allModeNames(): Map<Int, String> {
        val (fb, fn) = QcUltra2.GetAllModes
        val pkt      = BmapProtocol.build(fb, fn, Op.START)
        val raw      = transport.sendRecv(pkt, drain = true)
        val names    = QcUltra2.MODE_NAMES.toMutableMap()
        val (cfgFb, cfgFn) = QcUltra2.ModeConfigStatus
        for (r in BmapProtocol.parseAll(raw)) {
            if (r.fblock == cfgFb && r.func == cfgFn && r.op == Op.STATUS) {
                val (idx, name) = QcUltra2.parseModeConfigBasic(r.payload) ?: continue
                names[idx] = name
            }
        }
        return names
    }

    // ── Write operations ──────────────────────────────────────────────────────

    suspend fun setModeByIndex(index: Int) {
        val (fb, fn) = QcUltra2.CurrentMode
        val pkt = BmapProtocol.build(fb, fn, Op.START, byteArrayOf(index.toByte(), 0))
        throwIfError(BmapProtocol.parse(transport.sendRecv(pkt)))
    }

    suspend fun setCncLevel(level: Int) {
        require(level in 0..10) { "CNC level must be 0–10" }
        // ponytail: autoCnc=true lets the device manage level automatically → manual changes ignored.
        // Force autoCnc=false so the explicit level is applied.
        writeAudioSettings(audioSettings().copy(cncLevel = level, autoCnc = false))
    }

    suspend fun setWindBlock(enabled: Boolean) =
        writeAudioSettings(audioSettings().copy(windBlock = enabled))

    suspend fun setSpatial(mode: SpatialMode) =
        writeAudioSettings(audioSettings().copy(spatial = mode))

    suspend fun setAnc(enabled: Boolean) =
        writeAudioSettings(audioSettings().copy(ancToggle = enabled))

    suspend fun setSidetone(level: Int) {
        val (fb, fn) = QcUltra2.Sidetone
        val pkt = BmapProtocol.build(fb, fn, Op.SETGET, QcUltra2.buildSidetone(level))
        throwIfError(BmapProtocol.parse(transport.sendRecv(pkt)))
    }

    suspend fun setMultipoint(enabled: Boolean) {
        val (fb, fn) = QcUltra2.Multipoint
        val pkt = BmapProtocol.build(fb, fn, Op.SETGET, QcUltra2.buildToggle(enabled))
        throwIfError(BmapProtocol.parse(transport.sendRecv(pkt)))
    }

    suspend fun setEqBand(bandId: Int, value: Int) {
        require(value in -10..10) { "EQ value must be -10..+10" }
        val (fb, fn) = QcUltra2.Eq
        val pkt = BmapProtocol.build(fb, fn, Op.SETGET, QcUltra2.buildEqBand(value, bandId))
        throwIfError(BmapProtocol.parse(transport.sendRecv(pkt)))
    }

    suspend fun setDeviceName(name: String) {
        val (fb, fn) = QcUltra2.ProductName
        val pkt = BmapProtocol.build(fb, fn, Op.SETGET, QcUltra2.buildDeviceName(name))
        throwIfError(BmapProtocol.parse(transport.sendRecv(pkt)))
    }

    suspend fun switchToDevice(mac: String) {
        val (fb, fn) = QcUltra2.Routing
        val pkt = BmapProtocol.build(fb, fn, Op.START, QcUltra2.buildRouting(mac))
        throwIfError(BmapProtocol.parse(transport.sendRecv(pkt)))
    }

    suspend fun setFavorites(favSet: Set<Int>, totalModes: Int = 11) {
        val (fb, fn) = QcUltra2.Favorites
        val pkt = BmapProtocol.build(fb, fn, Op.SETGET, QcUltra2.buildFavorites(totalModes, favSet))
        throwIfError(BmapProtocol.parse(transport.sendRecv(pkt)))
    }

    suspend fun writeAudioSettings(s: AudioSettings) {
        val (fb, fn) = QcUltra2.AudioSettings
        val payload  = QcUltra2.buildAudioSettings(s)
        Log.d(TAG, "writeAudioSettings payload=${payload.hex()}")
        val pkt  = BmapProtocol.build(fb, fn, Op.SETGET, payload)
        val raw  = transport.sendRecv(pkt)
        Log.d(TAG, "writeAudioSettings response=${raw.hex()}")
        throwIfError(BmapProtocol.parse(raw))
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private suspend fun get(fblock: Byte, func: Byte): BmapResponse {
        val pkt  = BmapProtocol.build(fblock, func, Op.GET)
        Log.d(TAG, "GET [${fblock.hex()}.${func.hex()}] → ${pkt.hex()}")
        val raw  = transport.sendRecv(pkt)
        Log.d(TAG, "GET [${fblock.hex()}.${func.hex()}] ← ${raw.hex()}")
        val resp = BmapProtocol.parse(raw)
        Log.d(TAG, "GET parsed: ${resp?.format() ?: "null"}")
        throwIfError(resp)
        return resp ?: error("No response for GET [$fblock.$func]")
    }


    private fun throwIfError(resp: BmapResponse?) {
        if (resp?.op != Op.ERROR) return
        val code = if (resp.payload.isNotEmpty()) resp.payload[0] else 0
        throw BoseProtocolException("Device error: ${BmapError.name(code)} (${resp.format()})")
    }

    override fun close() = transport.close()
}

private fun Byte.hex() = "%02x".format(this)
private fun ByteArray.hex() = joinToString("") { "%02x".format(it) }

