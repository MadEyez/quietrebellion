/*
 * MainActivity.kt – Android UI for bosectl.
 */
package net.quietrebellion

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.ComponentName
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import android.util.Log
import android.view.Menu
import android.view.MenuItem
import android.view.View
import android.widget.RadioButton
import android.widget.SeekBar
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.app.AppCompatDelegate
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import net.quietrebellion.databinding.ActivityMainBinding

class MainActivity : AppCompatActivity() {

    private lateinit var b: ActivityMainBinding

    // ── Service binding ───────────────────────────────────────────────────────

    private var boseService: QuietRebellionService? = null
    private val serviceConn = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName, binder: IBinder) {
            boseService = (binder as QuietRebellionService.LocalBinder).service
            boseService?.clientBound = true
            val existingConn = boseService?.conn
            if (existingConn != null) {
                // Back from background – service already has a live connection
                lifecycleScope.launch { fetchAndRefresh(existingConn, b.tvDeviceName.text.toString()) }
            } else {
                ensurePermissionsThenConnect()
            }
            // ponytail: polls every 15s while app is open; ceiling: ~15s staleness for battery/mode.
            pollJob = lifecycleScope.launch {
                while (true) {
                    delay(15_000)
                    if (conn != null) fetchAndRefresh()
                }
            }
        }
        override fun onServiceDisconnected(name: ComponentName) { boseService = null }
    }

    // conn delegates to service; falls back to null if no service yet
    private val conn: BoseConnection? get() = boseService?.conn

    // ── Bluetooth adapter ─────────────────────────────────────────────────────
    private val btAdapter: BluetoothAdapter? by lazy {
        (getSystemService(BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter
    }

    // ── Cached state ──────────────────────────────────────────────────────────
    private var modeNames: Map<Int, String> = QcUltra2.MODE_NAMES
    private var currentModeIndex = -1
    private var audioSettings    = AudioSettings(0, true, SpatialMode.Off, false, false)
    private var eqBands          = listOf<EqBand>()
    private var sidetone         = 0
    private var multipoint       = false
    private var favorites        = setOf<Int>()
    private var favoritesTotalModes = 11
    private var pairedDevices     = mapOf<String, String>()
    private var activeMac: String? = null
    private var autoPlayPause     = false
    private var autoAnswer        = false

    // Guards against re-entrant UI updates triggering command sends
    private var updatingUi = false

    // ── Permission launcher ───────────────────────────────────────────────────
    private var pollJob: Job? = null
    private val permLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { grants ->
        // POST_NOTIFICATIONS is optional – only BLUETOOTH_CONNECT is required
        val btGranted = Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
            grants.getOrDefault(Manifest.permission.BLUETOOTH_CONNECT, true)
        if (btGranted) startAndBindService()
        else setStatus("Bluetooth permission required")
    }

    // ─────────────────────────────────────────────────────────────────────────

    override fun onCreate(savedInstanceState: Bundle?) {
        applyThemePref()
        super.onCreate(savedInstanceState)
        b = ActivityMainBinding.inflate(layoutInflater)
        setContentView(b.root)
        setSupportActionBar(b.toolbar)
        wireListeners()
        requestRequiredPermissions()
    }

    override fun onStop() {
        super.onStop()
        pollJob?.cancel(); pollJob = null
        boseService?.clientBound = false
        // Unbind but keep service running in background
        try { unbindService(serviceConn) } catch (_: Exception) {}
        boseService = null
    }

    override fun onStart() {
        super.onStart()
        // Only bind if service was already started (permission granted); startAndBindService() handles first launch.
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED) {
            bindService(Intent(this, QuietRebellionService::class.java), serviceConn, BIND_AUTO_CREATE)
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        // Don't close conn – service owns it
    }

    private fun startAndBindService() {
        val svcIntent = Intent(this, QuietRebellionService::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
            startForegroundService(svcIntent) else startService(svcIntent)
        bindService(svcIntent, serviceConn, BIND_AUTO_CREATE)
    }

    /** Requests all required runtime permissions in one shot via permLauncher. */
    private fun requestRequiredPermissions() {
        val needed = buildList {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
                checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED)
                add(Manifest.permission.BLUETOOTH_CONNECT)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
                add(Manifest.permission.POST_NOTIFICATIONS)
        }.toTypedArray()
        if (needed.isEmpty()) startAndBindService() else permLauncher.launch(needed)
    }


    // ── Theme toggle ──────────────────────────────────────────────────────────

    private fun applyThemePref() {
        val mode = getSharedPreferences("prefs", MODE_PRIVATE)
            .getInt("night_mode", AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM)
        AppCompatDelegate.setDefaultNightMode(mode)
    }

    private fun toggleTheme() {
        val isDark = AppCompatDelegate.getDefaultNightMode() == AppCompatDelegate.MODE_NIGHT_YES
        val newMode = if (isDark) AppCompatDelegate.MODE_NIGHT_NO else AppCompatDelegate.MODE_NIGHT_YES
        getSharedPreferences("prefs", MODE_PRIVATE).edit().putInt("night_mode", newMode).apply()
        AppCompatDelegate.setDefaultNightMode(newMode)
        recreate()
    }

    override fun onCreateOptionsMenu(menu: Menu): Boolean {
        menuInflater.inflate(R.menu.menu_main, menu)
        val isDark = AppCompatDelegate.getDefaultNightMode() == AppCompatDelegate.MODE_NIGHT_YES
        val iconRes = if (isDark) R.drawable.ic_sun else R.drawable.ic_moon
        menu.findItem(R.id.action_theme)?.setIcon(iconRes)
        return true
    }

    override fun onOptionsItemSelected(item: MenuItem): Boolean {
        if (item.itemId == R.id.action_theme) { toggleTheme(); return true }
        return super.onOptionsItemSelected(item)
    }

    // ── Connection lifecycle ──────────────────────────────────────────────────

    private fun ensurePermissionsThenConnect() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val needed = arrayOf(Manifest.permission.BLUETOOTH_CONNECT)
                .filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
                .toTypedArray()
            if (needed.isNotEmpty()) { permLauncher.launch(needed); return }
        }
        connect()
    }

    @android.annotation.SuppressLint("MissingPermission")
    private fun connect() {
        val adapter = btAdapter ?: return setStatus("Bluetooth not available")

        val devices = try {
            DeviceDiscovery.bondedDevices(adapter)
        } catch (e: SecurityException) {
            ensurePermissionsThenConnect(); return
        }
        if (devices.isEmpty()) return setStatus("No paired Bluetooth devices found")

        val lastMac = getSharedPreferences("prefs", MODE_PRIVATE).getString("last_mac", null)
        val preferred = lastMac?.let { mac -> devices.firstOrNull { it.address == mac } }

        if (preferred != null) {
            connectTo(preferred)
        } else if (devices.size == 1) {
            connectTo(devices[0])
        } else {
            val names = devices.map { it.name ?: it.address }.toTypedArray()
            AlertDialog.Builder(this)
                .setTitle("Select device")
                .setItems(names) { _, i -> connectTo(devices[i]) }
                .show()
        }
    }

    @android.annotation.SuppressLint("MissingPermission")
    private fun connectTo(device: android.bluetooth.BluetoothDevice) {
        lifecycleScope.launch {
            try {
                setStatus("Connecting to ${device.name}…")
                boseService?.detach()

                val transport = BluetoothTransport.connectDirect(device)
                val connection = BoseConnection(transport)
                Log.d("BoseCtl", "Socket connected to ${device.name}")

                getSharedPreferences("prefs", MODE_PRIVATE).edit()
                    .putString("last_mac", device.address).apply()


                b.tvDeviceName.text = device.name
                fetchAndRefresh(connection, device.name)
            } catch (e: Exception) {
                Log.e("BoseCtl", "Connection failed: ${e::class.simpleName}: ${e.message}", e)
                setStatus("Error: ${e.message}")
            }
        }
    }

    private suspend fun fetchAndRefresh(
        connection: BoseConnection? = conn,
        deviceName: String = b.tvDeviceName.text.toString(),
    ) {
        val c = connection ?: return
        try {
            modeNames        = c.allModeNames()
            currentModeIndex = c.modeIndex()
            audioSettings    = c.audioSettings()
            eqBands          = c.eq()
            sidetone         = c.sidetone()
            multipoint       = c.multipoint()
            val (favSet, favTotal) = try { c.favorites() } catch (_: Exception) { setOf<Int>() to 11 }
            favorites           = favSet
            favoritesTotalModes = favTotal
            val battery      = c.battery()
            val charging     = c.chargingState()
            val firmware     = try { c.firmware() } catch (_: Exception) { "" }

            val chargingIcon = if (charging == true) " ⚡" else ""
            b.tvBattery.text  = "Battery: $battery%$chargingIcon"
            b.tvFirmware.text = if (firmware.isNotEmpty()) "Firmware: $firmware" else ""
            setStatus("Connected")

            try {
                val (_, mac) = c.source()
                activeMac   = mac
                pairedDevices = c.pairedDevicesWithNames()
            } catch (_: Exception) { }

            autoPlayPause = try { c.autoPlayPause()   } catch (_: Exception) { false }
            autoAnswer    = try { c.autoAnswer()      } catch (_: Exception) { false }

            // Attach to service so it keeps running in background
            boseService?.attach(c, deviceName, battery, currentModeIndex,
                modeNames, audioSettings.ancToggle, favorites,
                source = activeMac?.let { pairedDevices[it] } ?: "")

            updateUi()
        } catch (e: Exception) {
            setStatus("Fetch error: ${e.message}")
        }
    }

    // ── UI population ─────────────────────────────────────────────────────────

    private fun updateUi() {
        updatingUi = true
        try {
            populateModes()
            applyCnc()
            applySpatial()
            b.swWindBlock.isChecked = audioSettings.windBlock
            applyEq()
            applySidetone()
            b.swMultipoint.isChecked = multipoint
            applyPairedDevices()
            b.swAutoPlayPause.isChecked = autoPlayPause
            b.swAutoAnswer.isChecked = autoAnswer
            applyNowPlaying()        } finally {
            updatingUi = false
        }
    }

    private fun populateModes() {
        b.rgModes.removeAllViews()
        modeNames.entries.sortedBy { it.key }
            .filter { (_, name) -> name.lowercase() != "none" }
            .forEach { (idx, name) ->
                val isFav = idx in favorites
                val rb = RadioButton(this).apply {
                    id   = View.generateViewId()
                    text = (if (isFav) "★ " else "") + name.replaceFirstChar { it.uppercaseChar() }
                    tag  = idx
                    if (idx == currentModeIndex) isChecked = true
                    setOnLongClickListener {
                    val newFavs = if (isFav) favorites - idx else favorites + idx
                    sendCmd {
                        conn!!.setFavorites(newFavs, favoritesTotalModes)
                        favorites = newFavs
                    }
                        true
                    }
                }
                b.rgModes.addView(rb)
            }
    }

    private fun applyCnc() {
        val auto = audioSettings.autoCnc
        // UI is inverted: slider 10 = max ANC, device stores 0 = max ANC
        val uiVal = 10 - audioSettings.cncLevel
        b.sbCnc.progress = uiVal
        b.tvCncLevel.text = "ANC: $uiVal"
        b.swAutoCnc.isChecked = auto
        b.layoutManualCnc.visibility = if (auto) View.GONE else View.VISIBLE
        b.tvAutoCncStatus.visibility = if (auto) View.VISIBLE else View.GONE
    }

    private fun applySpatial() {
        val rb = when (audioSettings.spatial) {
            SpatialMode.Off  -> b.rbSpatialOff
            SpatialMode.Room -> b.rbSpatialRoom
            SpatialMode.Head -> b.rbSpatialHead
        }
        b.rgSpatial.check(rb.id)
    }

    private fun applyEq() {
        for (band in eqBands) {
            val name = when (band.bandId) { 0 -> "Bass"; 1 -> "Mid"; else -> "Treble" }
            val tv   = when (band.bandId) { 0 -> b.tvBassVal; 1 -> b.tvMidVal; else -> b.tvTrebleVal }
            tv.text  = "$name: %+d dB".format(band.current)
            b.eqCurveView.setBand(band.bandId, band.current, band.minVal, band.maxVal)
        }
    }

    private fun applySidetone() {
        val rbId = when (sidetone) {
            1    -> b.rbSidetoneHigh.id
            2    -> b.rbSidetoneMedium.id
            3    -> b.rbSidetoneLow.id
            else -> b.rbSidetoneOff.id
        }
        b.rgSidetone.check(rbId)
    }

    private fun applyPairedDevices() {
        val others = pairedDevices.keys.filter { !it.equals(activeMac, ignoreCase = true) }
        val show = multipoint && activeMac != null && others.isNotEmpty()
        b.tvPairedDevicesLabel.visibility = if (show) View.VISIBLE else View.GONE
        b.tvActiveDevice.visibility       = if (show) View.VISIBLE else View.GONE
        b.tvPairedDevicesHint.visibility  = if (show) View.VISIBLE else View.GONE
        b.rgPairedDevices.visibility      = if (show) View.VISIBLE else View.GONE
        if (!show) return

        val activeName = activeMac?.let { pairedDevices[it] ?: shortenMac(it) } ?: "unknown"
        b.tvActiveDevice.text = "Now playing: $activeName"

        b.rgPairedDevices.removeAllViews()
        others.forEach { mac ->
            val label = pairedDevices[mac] ?: shortenMac(mac)
            val rb = RadioButton(this).apply {
                id  = View.generateViewId()
                text = label
                tag  = mac
            }
            b.rgPairedDevices.addView(rb)
        }
    }


    private fun applyNowPlaying() {
        val sourceName = activeMac?.let { pairedDevices[it] } ?: return run { b.tvNowPlaying.visibility = View.GONE }
        b.tvNowPlaying.text = sourceName
        b.tvNowPlaying.visibility = View.VISIBLE
    }


    // ── Wire UI listeners ─────────────────────────────────────────────────────

    private fun wireListeners() {
        b.btnReconnect.setOnClickListener { ensurePermissionsThenConnect() }

        b.toolbar.setOnMenuItemClickListener { item ->
            if (item.itemId == R.id.action_theme) { toggleTheme(); true } else false
        }

        b.btnRename.setOnClickListener {
            val cur = b.tvDeviceName.text.toString()
            val input = android.widget.EditText(this).apply { setText(cur) }
            AlertDialog.Builder(this)
                .setTitle("Rename Device")
                .setView(input)
                .setPositiveButton("OK") { _, _ ->
                    val newName = input.text.toString().trim()
                    if (newName.isNotEmpty() && newName != cur) sendCmd {
                        conn!!.setDeviceName(newName)
                        b.tvDeviceName.text = newName
                    }
                }
                .setNegativeButton("Cancel", null)
                .show()
        }

        b.rgModes.setOnCheckedChangeListener { group, checkedId ->
            if (updatingUi) return@setOnCheckedChangeListener
            val idx = group.findViewById<RadioButton>(checkedId)?.tag as? Int ?: return@setOnCheckedChangeListener
            sendCmd {
                conn!!.setModeByIndex(idx)
                currentModeIndex = idx
            }
        }

        b.sbCnc.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar, progress: Int, fromUser: Boolean) {
                if (!fromUser || updatingUi) return
                // invert: UI 10 = max ANC = device 0
                audioSettings = audioSettings.copy(cncLevel = 10 - progress)
                b.tvCncLevel.text = "ANC: $progress"
            }
            override fun onStartTrackingTouch(sb: SeekBar) = Unit
            override fun onStopTrackingTouch(sb: SeekBar) {
                if (updatingUi) return
                sendCmd { conn!!.setCncLevel(audioSettings.cncLevel) }
            }
        })

        b.rgSpatial.setOnCheckedChangeListener { _, checkedId ->
            if (updatingUi) return@setOnCheckedChangeListener
            val mode = when (checkedId) {
                b.rbSpatialRoom.id -> SpatialMode.Room
                b.rbSpatialHead.id -> SpatialMode.Head
                else               -> SpatialMode.Off
            }
            sendCmd { conn!!.setSpatial(mode); audioSettings = audioSettings.copy(spatial = mode) }
        }

        b.swWindBlock.setOnCheckedChangeListener { _, checked ->
            if (updatingUi) return@setOnCheckedChangeListener
            sendCmd { conn!!.setWindBlock(checked); audioSettings = audioSettings.copy(windBlock = checked) }
        }

        b.swAutoCnc.setOnCheckedChangeListener { _, checked ->
            if (updatingUi) return@setOnCheckedChangeListener
            sendCmd {
                val s = audioSettings.copy(autoCnc = checked)
                conn!!.writeAudioSettings(s)
                audioSettings = s
            }
        }

        b.eqCurveView.onBandChanged = { bandId, value ->
            val name = when (bandId) { 0 -> "Bass"; 1 -> "Mid"; else -> "Treble" }
            val tv   = when (bandId) { 0 -> b.tvBassVal; 1 -> b.tvMidVal; else -> b.tvTrebleVal }
            tv.text  = "$name: %+d dB".format(value)
        }
        b.eqCurveView.onBandReleased = { bandId, value ->
            sendCmd { conn!!.setEqBand(bandId, value) }
            eqBands = eqBands.map { if (it.bandId == bandId) it.copy(current = value) else it }
        }

        b.rgSidetone.setOnCheckedChangeListener { _, checkedId ->
            if (updatingUi) return@setOnCheckedChangeListener
            val level = when (checkedId) {
                b.rbSidetoneHigh.id   -> 1
                b.rbSidetoneMedium.id -> 2
                b.rbSidetoneLow.id    -> 3
                else                  -> 0
            }
            sendCmd { conn!!.setSidetone(level); sidetone = level }
        }

        b.swMultipoint.setOnCheckedChangeListener { _, checked ->
            if (updatingUi) return@setOnCheckedChangeListener
            sendCmd {
                conn!!.setMultipoint(checked)
                multipoint = checked
                applyPairedDevices()
            }
        }

        b.rgPairedDevices.setOnCheckedChangeListener { group, checkedId ->
            if (updatingUi) return@setOnCheckedChangeListener
            val mac = group.findViewById<RadioButton>(checkedId)?.tag as? String ?: return@setOnCheckedChangeListener
            sendCmd {
                conn!!.switchToDevice(mac)
                activeMac = try { conn!!.source().second ?: mac } catch (_: Exception) { mac }
            }
        }

        // Media controls

        b.btnPowerOff.setOnClickListener {
            AlertDialog.Builder(this)
                .setTitle("Power Off")
                .setMessage("Power off ${b.tvDeviceName.text}?")
                .setPositiveButton("Power Off") { _, _ ->
                    sendCmd { conn!!.powerOff() }
                }
                .setNegativeButton("Cancel", null)
                .show()
        }


        // Auto Play/Pause + Auto Answer
        b.swAutoPlayPause.setOnCheckedChangeListener { _, checked ->
            if (updatingUi) return@setOnCheckedChangeListener
            sendCmd { conn!!.setAutoPlayPause(checked); autoPlayPause = checked }
        }
        b.swAutoAnswer.setOnCheckedChangeListener { _, checked ->
            if (updatingUi) return@setOnCheckedChangeListener
            sendCmd { conn!!.setAutoAnswer(checked); autoAnswer = checked }
        }

        // Pairing mode
        b.btnPairingMode.setOnClickListener {
            AlertDialog.Builder(this)
                .setTitle("Pairing Mode")
                .setMessage("Enter Bluetooth pairing mode? The headphones will disconnect.")
                .setPositiveButton("Enter Pairing") { _, _ ->
                    sendCmd { conn!!.enterPairingMode() }
                }
                .setNegativeButton("Cancel", null)
                .show()
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /** Run a suspending BMAP command, then immediately refresh UI from device. */
    private fun sendCmd(block: suspend () -> Unit) {
        if (conn == null) { Toast.makeText(this, "Not connected", Toast.LENGTH_SHORT).show(); return }
        lifecycleScope.launch {
            try {
                block()
                fetchAndRefresh()
            } catch (e: Exception) {
                Toast.makeText(this@MainActivity, e.message ?: "Error", Toast.LENGTH_SHORT).show()
            }
        }
    }

    private fun setStatus(msg: String) { b.tvStatus.text = msg }

    private fun shortenMac(mac: String): String {
        val parts = mac.split(":")
        return if (parts.size >= 3) "…" + parts.takeLast(3).joinToString(":") else mac
    }
}
