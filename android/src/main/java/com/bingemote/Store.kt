package com.bingemote

import android.content.Context
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/**
 * App settings in plain preferences; secrets (VLC password, helper token) in
 * Keystore-backed encrypted preferences, same pattern as Listen Later's CredStore.
 */
/** The primary constructor doubles as a test seam: the real migration/settings
 *  code runs over an in-memory config, so a 1.1-era saved state can be
 *  exercised on the JVM without a Context. */
class Store internal constructor(
    private val prefs: android.content.SharedPreferences,
    private val secrets: android.content.SharedPreferences,
) {
    constructor(context: Context) : this(
        context.getSharedPreferences("settings", Context.MODE_PRIVATE),
        openSecrets(context),
    )

    init {
        // 1.9 removals: the seek bar is always on top and reconnect is always on.
        // Drop the retired keys so no stale setting lingers on existing installs.
        prefs.edit().remove("seekOnTop").remove("autoReconnect").apply()
    }

    /** True when the Keystore-backed store could not open this launch (teardown C11).
     *  NOTHING is ever auto-deleted: the encrypted file and the master key stay exactly
     *  as they are, the app keeps working on an in-memory stand-in, and recovery is a
     *  plain error + re-pair, by hand, never a wipe. */
    val secureStoreBroken: Boolean get() = secrets is InMemoryPrefs

    /** Fail-closed gate for every secret write: while the store is broken a write
     *  THROWS instead of landing in memory and vanishing at restart (no fake-persisted
     *  pairings). Reads still return "" so the app honestly shows unpaired. */
    private val secretBox = FailClosedSecrets(
        reader = { k -> secrets.getString(k, null) },
        writer = { k, v -> val ed = secrets.edit(); if (v == null) ed.remove(k) else ed.putString(k, v); ed.apply() },
        broken = secureStoreBroken,
    )

    var pcName: String
        get() = prefs.getString("pcName", "") ?: ""
        set(v) = prefs.edit().putString("pcName", v).apply()

    /** Last known helper/VLC address. Rediscovered over UDP when it changes. */
    var host: String
        get() = prefs.getString("host", "") ?: ""
        set(v) = prefs.edit().putString("host", v).apply()

    var vlcPort: Int
        get() = prefs.getInt("vlcPort", 8080)
        set(v) = prefs.edit().putInt("vlcPort", v).apply()

    var helperPort: Int
        get() = prefs.getInt("helperPort", 8492) // Zephyr VLC Remote default
        set(v) = prefs.edit().putInt("helperPort", v).apply()

    /** One-time notice after a 1.1 -> 1.2 upgrade retargeted the helper: the PC
     *  helper is now Zephyr VLC Remote. Dismissed forever on "Got it". */
    var vlcRemoteNoticePending: Boolean
        get() = prefs.getBoolean("vlcRemoteNoticePending", false)
        set(v) = prefs.edit().putBoolean("vlcRemoteNoticePending", v).apply()

    /** A 1.1 install saved the BingeMote helper port (8488). Zephyr VLC Remote
     *  imports BingeMote's pairings on the PC, so keep the saved token and just
     *  retarget the port; flag the one-time notice so the change is never silent
     *  (round-3 A3). Returns true only on the upgrade path. */
    fun migrateLegacyHelperPort(): Boolean {
        if (prefs.getInt("helperPort", 8492) != 8488) return false
        prefs.edit().putInt("helperPort", 8492).putBoolean("vlcRemoteNoticePending", true).apply()
        return true
    }

    /// Touchpad gain: 1.0 = raw finger movement. Range 0.5-2.0 in 0.1 steps (the steppers enforce it).
    var mouseSpeed: Float
        get() = prefs.getFloat("mouseSpeed", 1.0f)
        set(v) = prefs.edit().putFloat("mouseSpeed", v).apply()

    var moviesPath: String
        get() = normalizeWinPath(prefs.getString("moviesPath", "") ?: "")
        set(v) = prefs.edit().putString("moviesPath", normalizeWinPath(v)).apply()

    var skipMainSec: Int
        // A stored value that is no longer offered falls back to the default.
        get() = prefs.getInt("skipMainSec", 10).takeIf { it in MAIN_SKIP_OPTIONS } ?: 10
        set(v) = prefs.edit().putInt("skipMainSec", v).apply()

    var skipSecondarySec: Int
        get() = prefs.getInt("skipSecondarySec", 30).takeIf { it in SECONDARY_SKIP_OPTIONS } ?: 30
        set(v) = prefs.edit().putInt("skipSecondarySec", v).apply()

    var haptics: Boolean
        get() = prefs.getBoolean("haptics", true)
        set(v) = prefs.edit().putBoolean("haptics", v).apply()

    /** Notification permission was already requested once (teardown C1: asked once at
     *  the first phone stream on Android 13+, never nagged again). */
    var notifPermissionAsked: Boolean
        get() = prefs.getBoolean("notifPermissionAsked", false)
        set(v) = prefs.edit().putBoolean("notifPermissionAsked", v).apply()

    /** Last automatic update check (manual checks always run; teardown 9c throttle). */
    var lastUpdateCheckAt: Long
        get() = prefs.getLong("lastUpdateCheckAt", 0L)
        set(v) = prefs.edit().putLong("lastUpdateCheckAt", v).apply()

    /** Play/pause/skip + position on the lock screen while a movie plays. Off by default. */
    var lockScreenControls: Boolean
        get() = prefs.getBoolean("lockScreenControls", false)
        set(v) = prefs.edit().putBoolean("lockScreenControls", v).apply()

    // CI screenshot state (debug builds only): MediaControlsService seeds a demo session.
    var demoControls: Boolean
        get() = prefs.getBoolean("demoControls", false)
        set(v) = prefs.edit().putBoolean("demoControls", v).apply()

    /** Temporary A/B: the seek-drag cancel circle treatment. Try both
     * ("ring" = Ring only, "fills" = Fills in) and remove the loser; the setting goes with it. */
    var cancelStyle: String
        get() = prefs.getString("cancelStyle", "ring") ?: "ring"
        set(v) = prefs.edit().putString("cancelStyle", v).apply()

    /** Press flash on the home screen: 0 = skip rings only, 1 = every button. */
    var buttonFlash: Int
        get() = prefs.getInt("buttonFlash", 1).coerceIn(0, 1)
        set(v) = prefs.edit().putInt("buttonFlash", v).apply()

    var setupDone: Boolean
        get() = prefs.getBoolean("setupDone", false)
        set(v) = prefs.edit().putBoolean("setupDone", v).apply()

    /** A version the user chose to skip; not offered again. */
    var skippedUpdate: String?
        get() = prefs.getString("skipped_update", null)
        set(v) = prefs.edit().putString("skipped_update", v).apply()

    var vlcPassword: String
        get() = secretBox.read("vlcPassword")
        set(v) = secretBox.write("vlcPassword", v)

    /** The in-flight adoption request id, kept until the PC acks so a crash mid-fetch
     *  replays the same id instead of burning the one-shot grant. */
    var vlcAdoptRequestId: String?
        get() = secretBox.read("vlcAdoptRequestId").ifEmpty { null }
        set(v) = secretBox.write("vlcAdoptRequestId", v)

    /** Assigned Android movies folder (persisted SAF tree URI) for the Movies screen's Phone origin. */
    var moviesPhoneTreeUri: String
        get() = prefs.getString("moviesPhoneTreeUri", "") ?: ""
        set(v) = prefs.edit().putString("moviesPhoneTreeUri", v).apply()

    /** Zephyr Player source identity (paired separately from the BingeMote helper; port distinct). */
    /** Zephyr Player host, written by pairing. Blank = no Zephyr PC paired. */
    var zephyrHost: String
        get() = prefs.getString("zephyrHost", "") ?: ""
        set(v) = prefs.edit().putString("zephyrHost", v).apply()

    /** The paired Zephyr PC's address, or null when none is paired. NEVER falls back to
     *  another host (round-6 N-3): the pairing token may only ever go to the address it
     *  was paired with - "probably the same PC" is not authentication (S11). */
    val zephyrEndpoint: String?
        get() = zephyrHost.ifBlank { null }

    var zephyrPcName: String
        get() = prefs.getString("zephyrPcName", "") ?: ""
        set(v) = prefs.edit().putString("zephyrPcName", v).apply()

    var zephyrPort: Int
        get() = prefs.getInt("zephyrPort", 47823)
        set(v) = prefs.edit().putInt("zephyrPort", v).apply()
    // Pairing token is a secret: same Keystore-backed store as the helper token.
    // v13-dev builds wrote it to plain prefs; migrate on first read.
    var zephyrToken: String
        get() {
            val plain = prefs.getString("zephyrToken", "") ?: ""
            if (plain.isNotEmpty()) {
                // Migration only when the encrypted store is healthy; while broken the
                // plaintext value is honestly on disk, so it still reads, unmigrated.
                if (!secureStoreBroken) {
                    secretBox.write("zephyrToken", plain)
                    prefs.edit().remove("zephyrToken").apply()
                }
                return plain
            }
            return secretBox.read("zephyrToken")
        }
        set(v) = secretBox.write("zephyrToken", v)

    /** The phone's Connected Sources selection (protocol.md Source routing): which PC
     * target input frames go to. Default VLC = today's behavior; nothing re-routes
     * silently. */
    var selectedSource: Source
        get() = if (prefs.getString("selectedSource", "vlc") == "zephyr") Source.ZEPHYR else Source.VLC
        set(v) = prefs.edit().putString("selectedSource", if (v == Source.ZEPHYR) "zephyr" else "vlc").apply()

    var helperToken: String
        get() = secretBox.read("helperToken")
        set(v) = secretBox.write("helperToken", v)

    val isConfigured: Boolean
        get() = setupDone && host.isNotBlank() && vlcPassword.isNotBlank()

    /** Pairing state per source: the Zephyr PC rides on its own token, the VLC PC on a
     *  finished setup. Home gates its pair card and command sends on this (teardown A1). */
    fun isPaired(source: Source): Boolean =
        if (source == Source.ZEPHYR) zephyrToken.isNotBlank() else isConfigured
}

private fun openSecrets(context: Context): android.content.SharedPreferences = try {
    EncryptedSharedPreferences.create(
        context,
        "secrets",
        MasterKey.Builder(context).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build(),
        EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
        EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
    )
} catch (e: Exception) {
    InMemoryPrefs()
}

enum class Source { VLC, ZEPHYR }

/** VLC sometimes hands back mixed separators ("Desktop\/Movies"); store and show one clean form. Keeps a leading "\\" (network share) intact. */
private fun normalizeWinPath(p: String): String =
    // Lambda replacement on purpose: a "\" string replacement is an invalid escape and throws on a match.
    p.trim().replace('/', '\\').replace(Regex("(?<=.)\\\\{2,}")) { "\\" }

/** Every install opens on Home (teardown A1); pairing starts from Settings, never forced. */
internal fun initialScreenForConfiguredInstall(configured: Boolean): String = "main"


/** Read/write stand-in while the real encrypted store can't open: values live only
 *  for this process and are NEVER written to disk (no plaintext secrets, no deletes). */
private class InMemoryPrefs : android.content.SharedPreferences {
    private val map = HashMap<String, Any?>()
    override fun getAll(): MutableMap<String, *> = HashMap(map)
    override fun getString(key: String?, defValue: String?): String? = map[key] as? String ?: defValue
    override fun getStringSet(key: String?, defValues: MutableSet<String>?): MutableSet<String>? = defValues
    override fun getInt(key: String?, defValue: Int): Int = map[key] as? Int ?: defValue
    override fun getLong(key: String?, defValue: Long): Long = map[key] as? Long ?: defValue
    override fun getFloat(key: String?, defValue: Float): Float = map[key] as? Float ?: defValue
    override fun getBoolean(key: String?, defValue: Boolean): Boolean = map[key] as? Boolean ?: defValue
    override fun contains(key: String?): Boolean = map.containsKey(key)
    override fun edit(): android.content.SharedPreferences.Editor = Editor()
    override fun registerOnSharedPreferenceChangeListener(listener: android.content.SharedPreferences.OnSharedPreferenceChangeListener?) {}
    override fun unregisterOnSharedPreferenceChangeListener(listener: android.content.SharedPreferences.OnSharedPreferenceChangeListener?) {}
    private inner class Editor : android.content.SharedPreferences.Editor {
        private val writes = HashMap<String, Any?>()
        private val removals = HashSet<String>()
        private var clear = false
        override fun putString(key: String?, value: String?) = apply { key?.let { writes[it] = value; removals.remove(it) } }
        override fun putStringSet(key: String?, values: MutableSet<String>?) = apply { key?.let { writes[it] = values } }
        override fun putInt(key: String?, value: Int) = apply { key?.let { writes[it] = value; removals.remove(it) } }
        override fun putLong(key: String?, value: Long) = apply { key?.let { writes[it] = value; removals.remove(it) } }
        override fun putFloat(key: String?, value: Float) = apply { key?.let { writes[it] = value } }
        override fun putBoolean(key: String?, value: Boolean) = apply { key?.let { writes[it] = value; removals.remove(it) } }
        override fun remove(key: String?) = apply { key?.let { removals.add(it); writes.remove(it) } }
        override fun clear() = apply { clear = true }
        override fun commit(): Boolean { applyWrites(); return true }
        override fun apply() { applyWrites() }
        private fun applyWrites() {
            if (clear) map.clear()
            for (r in removals) map.remove(r)
            for ((k, v) in writes) map[k] = v
        }
    }
}
