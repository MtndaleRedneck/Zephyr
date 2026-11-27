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
        // Secure-only transport reads the loaded record from this store; unpaired, legacy, interrupted or unreadable yields null and nothing is sent.
        HelperClient.pairingSource = { (helperPairing() as? PairedCredential.State.Secure)?.credential }
        // 1.9 removals: the seek bar is always on top and reconnect is always on.
        // Drop the retired keys so no stale setting lingers on existing installs.
        prefs.edit().remove("seekOnTop").remove("autoReconnect").remove("cancelStyle").apply()
    }

    /** True when the Keystore-backed store could not open this launch (teardown C11).
     *  NOTHING is ever auto-deleted: the encrypted file and the master key stay exactly
     *  as they are, the app keeps working on an in-memory stand-in, and recovery is a
     *  plain error + re-pair, by hand, never a wipe. */
    private val storeOpenFailed: Boolean = secrets is InMemoryPrefs
    /** Failed to open this launch, or a secret read failed since (the box then refuses writes). */
    val secureStoreBroken: Boolean get() = storeOpenFailed || secretBox.isBroken

    /** Fail-closed gate for every secret write: while the store is broken a write
     *  THROWS instead of landing in memory and vanishing at restart (no fake-persisted
     *  pairings). Reads still return "" so the app honestly shows unpaired. */
    private val secretBox = FailClosedSecrets(
        reader = { k -> secrets.getString(k, null) },
        writer = { k, v -> val ed = secrets.edit(); if (v == null) ed.remove(k) else ed.putString(k, v); ed.apply() },
        broken = storeOpenFailed,
        onPoison = { PairingPublication.invalidate() },   // a failed read drops the published tuple at once: no stale good peek, no storage work here
    )

    /** Blocks until pending plain and secret writes are on disk; false when either commit is refused.
     *  Pairing uses it so "saved" means durable, not just queued. */
    fun commitPairing(): Boolean = try { prefs.edit().commit() && !secretBox.isBroken && secrets.edit().commit() } catch (e: Exception) { false }

    var pcName: String
        get() = prefs.getString("pcName", "") ?: ""
        set(v) = prefs.edit().putString("pcName", v).apply()

    /** Last known helper/VLC address. Rediscovered over UDP when it changes. */
    var host: String
        get() = (helperPairing() as? PairedCredential.State.Secure)?.credential?.host ?: (prefs.getString("host", "") ?: "")
        set(v) = pairingSection { prefs.edit().putString("host", v).apply() }

    var vlcPort: Int
        get() = prefs.getInt("vlcPort", 8080)
        set(v) = pairingSection { prefs.edit().putInt("vlcPort", v).apply() }

    var helperPort: Int
        get() = (helperPairing() as? PairedCredential.State.Secure)?.credential?.port ?: prefs.getInt("helperPort", 8492) // Zephyr VLC Remote default
        set(v) = pairingSection { prefs.edit().putInt("helperPort", v).apply() }

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
        pairingSection { prefs.edit().putInt("helperPort", 8492).putBoolean("vlcRemoteNoticePending", true).apply() }
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
    /** Keep the PC awake while a video is loaded in VLC (screen may still turn off;
     * minimized VLC sleeps normally). Default ON for new installs; a saved choice is kept.
     * The PC behavior itself ships with the reviewed helper change; helpers that don't
     * know the setting ignore it. */
    var keepAwake: Boolean
        get() = prefs.getBoolean("keepAwake", true)
        set(v) = prefs.edit().putBoolean("keepAwake", v).apply()

    var showVolumeOnPc: Boolean
        get() = prefs.getBoolean("showVolumeOnPc", true)
        set(v) = prefs.edit().putBoolean("showVolumeOnPc", v).apply()

    /** Play/pause/skip + position on the lock screen while a movie plays. Off by default. */
    var lockScreenControls: Boolean
        get() = prefs.getBoolean("lockScreenControls", false)
        set(v) = prefs.edit().putBoolean("lockScreenControls", v).apply()

    // CI screenshot state (debug builds only): MediaControlsService seeds a demo session.
    var demoControls: Boolean
        get() = prefs.getBoolean("demoControls", false)
        set(v) = prefs.edit().putBoolean("demoControls", v).apply()

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
        set(v) = pairingSection { secretBox.write("vlcPassword", v) }

    /** The in-flight adoption request id, kept until the PC acks so a crash mid-fetch
     *  replays the same id instead of burning the one-shot grant. */
    var vlcAdoptRequestId: String?
        get() = secretBox.read("vlcAdoptRequestId").ifEmpty { null }
        set(v) = secretBox.write("vlcAdoptRequestId", v)

    /** Assigned Android movies folder (persisted SAF tree URI) for the Movies screen's Phone origin. */
    var moviesPhoneTreeUri: String
        get() = prefs.getString("moviesPhoneTreeUri", "") ?: ""
        set(v) = prefs.edit().putString("moviesPhoneTreeUri", v).apply()

    /** Zephyr Player source identity (paired separately from the BingeMote helper; port distinct).
     *  Host, port and endpoint come only from the stored secure record (see [zephyrTarget]); old plain-HTTP prefs are never read. */
    val zephyrHost: String get() = zephyrTarget()?.host ?: ""

    /** The paired Zephyr PC's address, or null when none is paired. NEVER falls back to
     *  another host (round-6 N-3): the pairing token may only ever go to the address it
     *  was paired with - "probably the same PC" is not authentication (S11). */
    val zephyrEndpoint: String?
        get() = zephyrHost.ifBlank { null }

    /** The paired Player as ONE tuple. Immutable: an operation that holds one talks to exactly that host, port, token, local id and key. */
    class ZephyrTarget(val host: String, val port: Int, val token: String, val pcId: String = "", val pinHex: String = "") {
        fun same(o: ZephyrTarget?): Boolean = o != null && host == o.host && port == o.port && token == o.token && pcId == o.pcId && pinHex == o.pinHex
    }
    /** The process-wide record (shared by every Store instance): see [ZephyrRecord]. Only a Secure z1 record yields a target. */
    fun zephyrTarget(): ZephyrTarget? = try {
        zephyrRecord.read { targetOf(zephyrPairing()) }
    } catch (e: SecureStoreUnavailable) { null }
    private fun targetOf(st: ZephyrCredential.State): ZephyrTarget? =
        (st as? ZephyrCredential.State.Secure)?.credential?.let { ZephyrTarget(it.host, it.port, it.token, it.pcId, it.pinHex) }

    /** Zephyr pairing persistence: its own record, intent marker and latch (nothing shared with the helper but the write lock). */
    internal val zephyrPairStore: ZephyrPairStore = object : ZephyrPairStore {
        private fun syncWrite(k: String, v: String) {
            if (secretBox.isBroken) throw SecureStoreUnavailable()
            val ed = secrets.edit(); if (v.isEmpty()) ed.remove(k) else ed.putString(k, v)
            if (!ed.commit()) throw IllegalStateException("secret commit refused")
        }
        override var credential: String get() = secretBox.read("zephyrCredential"); set(v) = syncWrite("zephyrCredential", v)
        override var intent: String get() = secretBox.read("zephyrIntent"); set(v) = syncWrite("zephyrIntent", v)
        override var pcName: String get() = zephyrPcName; set(v) { zephyrPcName = v }
        override fun flush(): Boolean = commitPairing()
        override fun healthy(): Boolean = !secureStoreBroken
    }
    /** Presence only: an old plain-HTTP token still exists. Its value is never read. */
    private fun legacyZephyrPresent(): Boolean = !prefs.getString("zephyrToken", "").isNullOrEmpty() || secretBox.read("zephyrToken").isNotEmpty()
    /** Usable only when Secure. Legacy-only, interrupted, corrupt or unreadable storage is never usable. */
    internal fun zephyrPairing(): ZephyrCredential.State = ZephyrPairSave.load(zephyrPairStore) { legacyZephyrPresent() }

    /** The only writer of the Player pairing. [proof] carries the token and the key from one pairing connection; the local id is new for
     *  each pairing. Old plain-HTTP data is left exactly as it is on any outcome except a confirmed save, after which only the old token is removed. */
    internal fun saveZephyrPairing(proof: SecureHttp.BoundPin, host: String, port: Int, pcName: String): SecurePairSave.Outcome {
        var outcome = SecurePairSave.Outcome.FAILED_PRIOR_KEPT
        zephyrRecord.write {
            outcome = ZephyrPairSave.save(zephyrPairStore, host, port, pcName, proof, PairedCredential.newPcId())
            targetOf(zephyrPairing())
        }
        if (outcome == SecurePairSave.Outcome.SAVED) {
            runCatching { prefs.edit().remove("zephyrToken").apply(); secretBox.write("zephyrToken", "") }
        }
        return outcome
    }

    companion object {
        private val zephyrRecord = ZephyrRecord<ZephyrTarget>()
        /** Test-only: forget the process-shared cache so each test starts unloaded. */
        internal fun resetZephyrRecordForTests() = zephyrRecord.resetForTests()
        init { installZephyrPinSource() }
        /** Installs the client's whole-tuple gate over the shared record (also called by tests that need the real gate back after another test cleared it). */
        internal fun installZephyrPinSource() {
            // Set once when the class loads, from the shared record, never from an instance. Reads nothing from disk; nothing is sent while a write is in flight.
            com.zephyr.remote.ZephyrClient.pinSource = com.zephyr.remote.ZephyrClient.PinSource { t ->
                when (val s = zephyrRecord.peekState()) {
                    is ZephyrRecord.Peek.Unavailable -> com.zephyr.remote.ZephyrClient.Pin.UNAVAILABLE   // no verdict on the pairing
                    is ZephyrRecord.Peek.Value -> if (t.same(s.v)) com.zephyr.remote.ZephyrClient.Pin.MATCH else com.zephyr.remote.ZephyrClient.Pin.CHANGED
                }
            }
        }
    }

    var zephyrPcName: String
        get() = prefs.getString("zephyrPcName", "") ?: ""
        private set(v) = prefs.edit().putString("zephyrPcName", v).apply()

    val zephyrPort: Int get() = zephyrTarget()?.port ?: 47823

    /** The phone's Connected Sources selection (protocol.md Source routing): which PC
     * target input frames go to. Default VLC = today's behavior; nothing re-routes
     * silently. */
    var selectedSource: Source
        get() = if (prefs.getString("selectedSource", "vlc") == "zephyr") Source.ZEPHYR else Source.VLC
        set(v) = prefs.edit().putString("selectedSource", if (v == Source.ZEPHYR) "zephyr" else "vlc").apply()

    /** The secure record's token when one is loaded; otherwise the raw old value, which the secure transport never sends. */
    var helperToken: String
        get() = (helperPairing() as? PairedCredential.State.Secure)?.credential?.token ?: secretBox.read("helperToken")
        set(v) = pairingSection { secretBox.write("helperToken", v) }

    /** Every write of a helper/VLC tuple field runs in a writer section (see [PairingPublication]); a setter used on its own is its own section. */
    internal fun <T> pairingSection(block: () -> T): T = SecurePairSave.writerSection(securePairStore, block)
    /** The whole tuple exactly as the getters return it, for any pairing state, plus the broken check the publication needs. */
    private val tupleSource = object : PairingPublicationSource {
        override fun loadTuple(): HelperVlcSnapshot {
            val c = (helperPairing() as? PairedCredential.State.Secure)?.credential
            val ep = c?.let { HelperEndpoint(it.host, it.port, it.token) } ?: HelperEndpoint(host, helperPort, helperToken)
            return HelperVlcSnapshot(ep, vlcPort, vlcPassword, secure = c != null)
        }
        override fun isBroken() = secureStoreBroken
    }
    /** Lock-free, storage-free: the published tuple, or null while unloaded or a writer section is in flight (treat as not current). */
    fun pairingPeek(): HelperVlcSnapshot? = PairingPublication.peek()

    /** Secure pairing persistence (credential record + write-ahead marker), same secret store and flush as the token. */
    internal val securePairStore: SecurePairStore = object : SecurePairStore {
        // Each write is a synchronous commit() that must report success; a refused commit throws, so the save logic sees it.
        private fun syncWrite(k: String, v: String) {
            if (secretBox.isBroken) throw SecureStoreUnavailable()
            val ed = secrets.edit(); if (v.isEmpty()) ed.remove(k) else ed.putString(k, v)
            if (!ed.commit()) throw IllegalStateException("secret commit refused")
        }
        override var credential: String get() = secretBox.read("helperCredential"); set(v) = syncWrite("helperCredential", v)
        override var intent: String get() = secretBox.read("helperIntent"); set(v) = syncWrite("helperIntent", v)
        override var vlcPassword: String get() = this@Store.vlcPassword; set(v) { this@Store.vlcPassword = v }
        override var pcName: String get() = this@Store.pcName; set(v) { this@Store.pcName = v }
        override var legacyToken: String get() = secretBox.read("helperToken"); set(v) = secretBox.write("helperToken", v)
        override fun flush(): Boolean = commitPairing()
        override fun healthy(): Boolean = !secureStoreBroken
        override val publication: PairingPublicationSource get() = tupleSource
    }
    /** Usable only when Secure. Legacy-only, interrupted, corrupt or unreadable storage is never usable. */
    internal fun helperPairing(): PairedCredential.State = SecurePairSave.load(securePairStore)

    /** host, port and token read from ONE load of the stored record, for any operation that needs all three. Separate getters
     *  each load again, so a move between two of them could mix an old host with a new port. Not secure = plain fallback values, which
     *  the secure transport never sends. */
    class HelperEndpoint(val host: String, val port: Int, val token: String)
    /** Runs [block] with host, port and token from one load of the stored record. */
    inline fun <T> withHelper(block: (String, Int, String) -> T): T { val e = helperEndpoint(); return block(e.host, e.port, e.token) }
    /** Helper endpoint plus the VLC port and password, read together under the pairing write lock so a re-pair cannot land between the reads
     *  (a re-pair writes the record and the VLC password inside that lock). Local reads only: no network, no suspension. */
    class HelperVlcSnapshot(val endpoint: HelperEndpoint, val vlcPort: Int, val vlcPassword: String,
                            /** True only when the endpoint came from a loaded Secure pairing record (host and port verified with the key); false for the plain fallback. */
                            val secure: Boolean = false) {
        /** Same PC for the Main guards. Secure pairing on both sides: token, VLC port and VLC password (a verified address move,
         *  SecurePairSave.updateAddress, keeps pin and token and changes only host and helper port: the same PC). Both plain/legacy: the whole
         *  [sameTarget], including host and helper port, because no verified move exists there. A secure/plain mismatch is never the same PC. */
        fun samePc(o: HelperVlcSnapshot?): Boolean = when {
            o == null || secure != o.secure -> false
            secure -> endpoint.token == o.endpoint.token && vlcPort == o.vlcPort && vlcPassword == o.vlcPassword
            else -> sameTarget(o)
        }
        /** The whole direct-VLC and helper target: address, helper port, token, VLC port and password. */
        fun sameTarget(o: HelperVlcSnapshot?): Boolean = o != null && endpoint.host == o.endpoint.host && endpoint.port == o.endpoint.port &&
            endpoint.token == o.endpoint.token && vlcPort == o.vlcPort && vlcPassword == o.vlcPassword
        inline fun <T> withVlc(block: (String, Int, String) -> T): T = block(endpoint.host, vlcPort, vlcPassword)
        inline fun <T> withHelper(block: (String, Int, String) -> T): T = block(endpoint.host, endpoint.port, endpoint.token)
    }
    fun helperVlcSnapshot(): HelperVlcSnapshot = SecurePairSave.readAndPublish(tupleSource)
    /** Runs [block] with the VLC host, port and password from one locked read (see [helperVlcSnapshot]). The host is the stored pairing's, or the
     *  plain saved one when there is no secure pairing, as [host] reads it, so direct-VLC use needs no helper pairing. */
    inline fun <T> withVlc(block: (String, Int, String) -> T): T { val s = helperVlcSnapshot(); return block(s.endpoint.host, s.vlcPort, s.vlcPassword) }
    fun helperEndpoint(): HelperEndpoint =
        (helperPairing() as? PairedCredential.State.Secure)?.credential?.let { HelperEndpoint(it.host, it.port, it.token) }
            ?: HelperEndpoint(host, helperPort, helperToken)

    val isConfigured: Boolean
        get() = setupDone && helperPairing() is PairedCredential.State.Secure && host.isNotBlank() && vlcPassword.isNotBlank()

    /** An old plain-HTTP pairing, an interrupted save or an unreadable store: shown as "pair again", never as paired. */
    val pairingNeedsRepair: Boolean
        get() = setupDone && helperPairing().let { it is PairedCredential.State.NeedsRepair || it is PairedCredential.State.Blocked }

    /** Pairing state per source: the Zephyr PC rides on its own token, the VLC PC on a
     *  finished setup. Home gates its pair card and command sends on this (teardown A1). */
    fun isPaired(source: Source): Boolean =
        if (source == Source.ZEPHYR) zephyrTarget() != null else isConfigured
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
