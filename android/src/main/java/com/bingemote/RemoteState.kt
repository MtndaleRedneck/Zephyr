package com.bingemote

import com.zephyr.remote.ZephyrClient
import android.content.Context
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Live connection to VLC: polls status about once a second while the app is open,
 * rediscovers the PC over UDP when the address goes stale, and turns two failed
 * polls in a row into the error card.
 */
class RemoteState(
    private val app: Context,
    val store: Store,
    private val scope: CoroutineScope,
    /** Startup work that must finish before the pairing tuple is first published or polled (the 1.1 port migration, debug seeds). */
    private val startup: kotlinx.coroutines.Job? = null,
    /** Where the first-publication preload (the one init coroutine below) runs; used nowhere else. Production keeps IO; a test passes a queued dispatcher to step it. */
    ioDispatcher: kotlinx.coroutines.CoroutineDispatcher = Dispatchers.IO,
) {
    var status by mutableStateOf<VlcStatus?>(null)
        private set
    var unreachable by mutableStateOf(false)
        private set

    /** Either service answering proves the PC is reachable, independently of VLC playback. */
    val pcReachable: Boolean get() = !unreachable || (store.helperToken.isNotBlank() && !helperDown)

    /** Status as the home screen presents it. The raw status is the last good poll and
     *  survives a dropped connection so a blip loses nothing; but once the player is
     *  unreachable (or VLC itself is gone/refused) the seek bar, times and play glyph
     *  read zero/play instead of stale playback. The cached status stays for an instant
     *  recovery when the connection returns (BingeMote 1.15 parity). */
    val shownStatus: VlcStatus?
        get() = if (unreachable || vlcClosed || vlcRefused) null else status

    /** VLC didn't answer but the helper did: the PC is fine, VLC just isn't open. */
    var vlcClosed by mutableStateOf(false)
    /** VLC refused the password and the one automatic heal (the adoption fetch) is spent:
     *  the card says so and points at re-pair or the PC-side setting. */
    var vlcRefused by mutableStateOf(false)
    /** The helper just started VLC: hide the error card while it comes up. */
    var expectingVlc by mutableStateOf(false)
        private set

    /** The helper just started VLC: hide the error card for up to 20s while it comes up. */
    fun expectVlcStarting() {
        expectingVlc = true
        scope.launch {
            kotlinx.coroutines.delay(20_000)
            expectingVlc = false
        }
    }
    var connecting by mutableStateOf(false)
        private set

    /** Local mute: VLC has no mute command, so we send volume 0 and remember where we were. */
    var muted by mutableStateOf(false)
        private set

    /** Windows system volume 0-100 from the helper; null when the helper is unreachable, and VLC volume is used instead. */
    var winVolume by mutableStateOf<Int?>(null)
        private set

    /** True when the helper is paired but not answering, so the volume row fell back to VLC volume. */
    var helperDown by mutableStateOf(false)
        private set
    // The PC answered 401: this phone's pairing was removed. Definitive, not a blip.
    var unpaired by mutableStateOf(false)
    /** The PC presented a different security key than the one remembered at pairing. Never cleared by a retry: only a fresh pairing replaces the record. */
    var keyChanged by mutableStateOf(false)
        private set
    // The helper-down NOTICE waits ~5s of continuous failure; the VLC volume
    // fallback (2 strikes) still kicks in immediately - a brief blip falls back
    // quietly without nagging.
    var helperNotice by mutableStateOf(false)
        private set
    private var helperDownAt = 0L
    // The pairing token the notice state above belongs to (see pollOnce).
    private var noticeToken: String? = null
    private var preMuteRaw = 0

    /** Last subtitle track picked in the app, per media title. VLC's status doesn't report the current track on all versions. */
    var pickedSubtitle by mutableStateOf<Int?>(null)

    /** The address the live mouse connection uses. Store.host is plain preferences, so a
     *  rediscovery while the mouse screen is open never recomposes it - publish here instead. */
    var liveHost by mutableStateOf(store.host); private set
    var livePort by mutableStateOf(store.helperPort); private set
    var liveToken by mutableStateOf(store.helperToken); private set

    /** A successful re-pair replaces the PC identity: no pending or showing "moved" notice about the old PC may survive it. Main thread. */
    fun onRePaired() {
        movedGate.clear(); helperMovedTo = null
        // Poll-derived state described the old PC. Reset it to "connecting" here, on Main, after the pairing write (which ran on IO before this):
        // a stale poll block either ran earlier (this overwrites it) or sees a different token and publishes nothing.
        status = null; failures = 0; helperFailures = 0; helperDownAt = 0L; closedPolls = 0; lastConnectedAt = 0L
        unreachable = false; vlcClosed = false; vlcRefused = false; helperDown = false; helperNotice = false; unpaired = false
        winVolume = null; connecting = true; muted = false; keyChanged = false   // keyChanged is recomputed from shared state on the next poll
    }

    /** A SAVED Player pairing (the pairing screen's real save path) replaces the PC identity: a pending or showing Player "moved" notice and the pair-again hint
     *  described the old pairing. The helper's [onRePaired] does not stand in for this. Main thread. */
    fun onZephyrRePaired() { zephyrGate.clear(); zephyrMovedTo = null; zephyrPairAgainFor = null }

    /** Main thread only, no suspension between the read and the setter: publishes the stored record, optionally only while it is still [expectedRecord]. */
    fun publishIfCurrent(expectedRecord: String?): Boolean = LivePublication.publishIfCurrent(store.securePairStore, expectedRecord, ::publishAddress)

    /** Pairing, re-pairing or rediscovery learned an address: open screens reconnect to it. */
    fun publishAddress(host: String, port: Int, token: String) {
        liveHost = host; livePort = port; liveToken = token
    }

    /** Screenshot-rig only: a fake playing video so seek-drag captures show real times. */
    fun debugSeedStatus() {
        if (BuildConfig.DEBUG) status = VlcStatus("playing", "Demo", 1500, 6624, 128, false, emptyList())
    }

    /** Screenshot-rig only: renders the REAL shipped "Player moved" notice branch (the same state the authenticated-poll gate publishes). */
    fun debugShowZephyrMoved(name: String) {
        if (BuildConfig.DEBUG) zephyrMovedTo = name
    }

    /** Screenshot-rig only: mock hero note line for the status-notice proposal
     * renders (the proposed E/unpaired subtitle). Never set outside the rig. */
    var mockHeroNote by mutableStateOf<String?>(null)

    /** Screenshot-rig only: force the connection surfaces so captures show the real
     * status UI in each review scenario. */
    fun debugForceStatus(unreachable: Boolean, helperDown: Boolean, helperNotice: Boolean = false, unpaired: Boolean = false) {
        if (BuildConfig.DEBUG) {
            this.unreachable = unreachable; this.helperDown = helperDown
            this.helperNotice = helperNotice; this.unpaired = unpaired
        }
    }

    private var pollJob: Job? = null
    private var lastConnectedAt = 0L
    private var failures = 0
    private var helperFailures = 0
    /** One atomic admission (in-flight flag + 60 s gap) for every rediscover() caller. */
    private val helperAdmission = RediscoverAdmission()
    private val movedGate = MovedNoticeGate()
    /** S11b: the saved helper appears at a NEW address. The saved token never
     *  follows a discovered host; the hero offers the safe re-pair path instead.
     *  Cleared on a good helper poll and on source switches. */
    var helperMovedTo by mutableStateOf<String?>(null); private set
    private var closedPolls = 0
    private var lastTitle: String? = null
    private var lastLengthSec: Long = -1

    private companion object {
        /** Consecutive failed polls before the Disconnected card shows; one success clears it. */
        const val UNREACHABLE_AFTER = 2
    }

    /** One immediate retry on a network blip: a single dropped packet must not count as a failure.
     *  A connect timeout is not a blip - the PC is gone - so it is not retried (would double the wait). */
    private suspend fun <T> twiceIO(block: () -> T): T = withContext(Dispatchers.IO) {
        try { block() }
        catch (e: Vlc.BadAuth) { throw e }
        catch (e: Vlc.ConnectTimeout) { throw e }
        catch (e: HelperClient.ConnectTimeout) { throw e }
        catch (e: Exception) { block() }
    }

    fun start() {
        if (pollJob?.isActive == true) return
        pollJob = scope.launch {
            while (isActive) {
                pollOnce()
                delay(1000)
            }
        }
    }

    fun stop() {
        pollJob?.cancel()
        pollJob = null
    }

    fun selectSource(source: Source) {
        if (store.selectedSource == source) return
        store.selectedSource = source
        MediaControls.sync(app, store)
        status = null; winVolume = null; pickedSubtitle = null
        // Helper-only subtitle work must not outlive the source it belonged to.
        subtitleRequest++; subtitleListRequest++; subtitleFolder = null; pickedSubtitleFile = null
        subtitleListLoading = false; subtitleMutationLoading = false; subtitleListInFlight = false
        muted = false; vlcClosed = false; vlcRefused = false
        helperDown = false; helperNotice = false; helperMovedTo = null; movedGate.clear(); unpaired = false
        zephyrGate.clear(); zephyrMovedTo = null; zephyrPairAgainFor = null
        failures = 0; connecting = true; unreachable = true
        retry()
    }

    // N-3: no fallback to the helper host - the Zephyr bearer only ever goes to
    // the address it was paired with. (Blank simply fails the poll while unpaired.)
    /** The Player target of the command now running (null for a non-Zephyr command), captured at tap time and re-matched at execution. */
    @Volatile private var runZ: Store.ZephyrTarget? = null
    private fun zt(): Store.ZephyrTarget = runZ ?: throw java.io.IOException("Zephyr PC isn't paired")

    private suspend fun pollZephyr() {
        val zPoll = withContext(Dispatchers.IO) { store.zephyrTarget() }
        if (zPoll == null) {
            unreachable = true; connecting = false; return
        }
        try {
            val json = withContext(Dispatchers.IO) {
                ZephyrClient.controlStatus(zPoll)
            }
            if (store.selectedSource != Source.ZEPHYR) return
            // The pairing changed during the request: this answer belongs to the old PC, so it writes nothing.
            if (!zPoll.same(store.zephyrTarget())) return
            // Trust the phone's own volume/mute taps briefly: the poll used to snap the
            // number back before the command landed (teardown C13).
            val polledVol = (json.optInt("volume", 100).coerceIn(0, 100) * 256 / 100)
            val vol = if (System.currentTimeMillis() - localVolumeAt <= 1500) status?.volumeRaw ?: polledVol else polledVol
            val title = json.optString("title", "")
            val length = json.optLong("length")
            status = VlcStatus(json.optString("state", "stopped"), title,
                json.optLong("time"), length, vol,
                json.optBoolean("fullscreen"), ZephyrClient.parseSpuTracks(json))
            if (System.currentTimeMillis() - localMuteAt > 1500) muted = json.optBoolean("muted")
            // A new video clears the remembered subtitle pick, same as the VLC path.
            if (title != lastTitle || length != lastLengthSec) {
                lastTitle = title; lastLengthSec = length
                pickedSubtitle = null
            }
            connecting = false
            // Play/pause is inert until a poll has just succeeded (see togglePause); the Zephyr
            // path must stamp it too or the button never fires.
            lastConnectedAt = android.os.SystemClock.elapsedRealtime()
            // A good poll also clears "pair again" (N2): after a re-pair the hero must
            // not keep accusing the NEW pairing until an app restart.
            PairingStatePolicy.onSuccess().also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures }
            onZephyrAuthenticatedPoll(zPoll)
            vlcClosed = false; vlcRefused = false; helperNotice = false; helperDown = false
        } catch (e: ZephyrClient.BadToken) {
            if (store.selectedSource != Source.ZEPHYR || !zPoll.same(store.zephyrTarget())) return
            // The PC removed this phone's pairing - say "pair again", not "can't reach"
            // (N2: a 401 is NOT an outage, and must clear any outage already showing).
            connecting = false
            lastConnectedAt = 0L
            PairingStatePolicy.onBadToken().also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures }
        } catch (e: Exception) {
            if (store.selectedSource != Source.ZEPHYR || !zPoll.same(store.zephyrTarget())) return
            // A refused pin check says nothing about the PC: no outage count, no "pair again", no rediscovery.
            if (e is ZephyrClient.PairingChanged || e is ZephyrClient.PinUnavailable) return
            // A DHCP address change looks like an outage: re-find the player and keep
            // the saved pairing (teardown C3). The token is the PC's identity.
            if (zephyrNoteFailure()) zephyrRediscover()
        }
    }

    /** One failed Player poll: counts toward the outage card. Only an authenticated success (or a verified move) ever resets this; discovery never does. True when a rediscovery is due. */
    internal fun zephyrNoteFailure(): Boolean {
        PairingStatePolicy.onFailure(PairingStatePolicy.Flags(unpaired, unreachable, failures), UNREACHABLE_AFTER)
            .also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures }
        connecting = false
        return failures >= 2
    }

    /** The sole publisher of [zephyrMovedTo]: an authenticated poll that used [t] (the endpoint that poll actually targeted). Main thread. */
    internal fun onZephyrAuthenticatedPoll(t: Store.ZephyrTarget, now: Long = System.currentTimeMillis()) {
        zephyrMovedTo = zephyrGate.onAuthenticatedPoll(now, zephyrEndpointKey(t.host, t.port, t.token))
        zephyrPairAgainFor = null
    }
    private fun zephyrEndpointKey(host: String, port: Int, token: String) = HelperRediscover.addressKey(host, port) + "|" + token

    /** Internal for tests: current failure count and outage flag as the hero reads them. */
    internal val zephyrFailureCount: Int get() = failures

    /** Separate atomic admission (one attempt in flight + 60 s gap) for the Player; the helper's is its own, so neither blocks the other. */
    private val zephyrAdmission = RediscoverAdmission()
    /** Own gate instance: a Player move can never be confirmed by a helper poll or the reverse. */
    private val zephyrGate = MovedNoticeGate()
    /** The Player moved to a new address and the phone followed it by proving the stored key. Shown only after an authenticated poll on the moved endpoint
     *  ([onZephyrAuthenticatedPoll] is its only writer besides clears). */
    var zephyrMovedTo by mutableStateOf<String?>(null); private set
    /** A Player with the saved name answered at another address but did NOT prove the stored key (a renamed-key PC or an impostor): the phone moved nothing
     *  and says to pair again. A label only; nothing follows it. Cleared on an authenticated poll, a source switch and a Player re-pair. */
    var zephyrPairAgainFor by mutableStateOf<String?>(null); private set

    private fun zephyrRediscover() {
        // Back off and one-in-flight through the atomic admission; its completion handler is the only release.
        zephyrAdmission.launchIfAdmitted(scope, Dispatchers.IO) { zephyrAttempt() }
    }

    /** The body of one admitted attempt (the production code path; the parameters default to production and exist so a test can drive it with a stub port and no network).
     *  The saved token never goes to a discovered host: the handshake carries no application bytes, only the stored key decides (ZephyrMove).
     *  A cancel seen after the probe stops before the store is touched; a cancelled coroutine never reaches the Main apply block (withContext checks cancellation on entry),
     *  so a write that already started is whole old or whole new and raises no notice and no counter change. */
    internal suspend fun zephyrAttempt(
        port: ZephyrMove.Port = store.zephyrMovePort(),
        pingSaved: (ZephyrCredential) -> Boolean = { c -> ZephyrMove.handshakeMatches(c, c.host, c.port) },
        discover: () -> List<DiscoveredHelper> = { ZephyrClient.discoverPlayers(3000) },
        verify: (ZephyrCredential, String, Int) -> Boolean = { c, h, p -> ZephyrMove.handshakeMatches(c, h, p) },
        addressOk: (String) -> Boolean = HelperRediscover::isPrivateLiteral,
        main: kotlin.coroutines.CoroutineContext = Dispatchers.Main,
    ) {
        val before = store.zephyrTarget() ?: return
        val ctx = kotlin.coroutines.coroutineContext
        val out = ZephyrMove.attempt(port, pingSaved, discover, verify, addressOk, ensureActive = { ctx.ensureActive() })
        withContext(main) { applyZephyrMove(before, out) }
    }

    /** Main thread: the effects of one attempt. [before] is the target the attempt started from. */
    internal fun applyZephyrMove(before: Store.ZephyrTarget, out: ZephyrMove.Outcome) {
        if (store.selectedSource != Source.ZEPHYR) return
        when (out) {
            is ZephyrMove.Outcome.Moved -> {
                // Our own move is accepted only when the stored pairing now IS the committed tuple (same id, key and token as before, new host/port).
                // A re-pair, another move or a forget meanwhile is an external mutation: nothing here, no notice about the old PC.
                val c = out.credential
                val committedTuple = Store.ZephyrTarget(c.host, c.port, c.token, c.pcId, c.pinHex)
                if (c.pcId != before.pcId || c.pinHex != before.pinHex || c.token != before.token) return
                if (!committedTuple.same(store.zephyrTarget())) return
                // The candidate proved the stored key by handshake, so this verified move may clear the outage count; the NOTICE still waits for an authenticated poll.
                failures = 0; unreachable = false
                if (out.committed) zephyrGate.onDurableMove(out.pcName.ifBlank { c.host }, zephyrEndpointKey(c.host, c.port, c.token), System.currentTimeMillis())
            }
            is ZephyrMove.Outcome.NoneVerified ->
                if (before.same(store.zephyrTarget())) zephyrPairAgainFor = out.sameNameSeen
            else -> { /* saved endpoint seen, refused, blocked (the store already says pair again) or not paired: no counter or notice changes */ }
        }
    }

    // S13: pairing state as Compose state. The keystore-backed token decrypt used to
    // run inside composition (MainScreen pill + hero) several times a second on the
    // main thread; now it refreshes once per poll and right after pair/clear flows.
    var zephyrPaired by mutableStateOf(false); private set
    var vlcPaired by mutableStateOf(false); private set
    /** Screenshot-rig only: render the VLC source as paired without a real pairing. Never set outside the rig. */
    @Volatile var debugForcePaired = false

    init { refreshPairing() } // once at creation; then per poll + after pair/clear
    init { scope.launch(ioDispatcher) { try { startup?.join(); store.helperVlcSnapshot() } catch (e: Exception) { } } }   // first publication of the pairing tuple, off Main
    fun refreshPairing() {
        zephyrPaired = store.isPaired(Source.ZEPHYR)
        vlcPaired = store.isPaired(Source.VLC) || (BuildConfig.DEBUG && debugForcePaired)
    }
    fun pairedForSelected(): Boolean = if (store.selectedSource == Source.ZEPHYR) zephyrPaired else vlcPaired

    /** True while the stored pairing is still the PC this poll targeted (token identity; a move keeps it). Plain local read, no suspension. */
    private fun samePollPc(snap: Store.HelperVlcSnapshot) = snap.samePc(store.pairingPeek())   // null peek (unloaded or a writer in flight) = not current: the outcome is dropped, never counted

    suspend fun pollOnce() {
        startup?.join()   // no poll runs before the startup migration finished (already complete after the first call)
        refreshPairing()
        if (store.selectedSource == Source.ZEPHYR) { pollZephyr(); return }
        // The helper-notice state belongs to the pairing it was learned from: a new
        // or cleared token makes it stale. Reset it here so a re-paired phone never
        // sees "pair again" (or a leftover helper warning) about its NEW pairing.
        // Runs before the isConfigured early-return so a cleared token also resets.
        var pub = store.pairingPeek()
        if (pub == null) { withContext(Dispatchers.IO) { store.helperVlcSnapshot() }; pub = store.pairingPeek() }   // reader publish off Main; still null = writer in flight, skip the compare this poll
        if (pub != null && noticeToken != pub.endpoint.token) {
            noticeToken = pub.endpoint.token
            unpaired = false
            helperNotice = false
            helperDown = false
            helperFailures = 0
            helperDownAt = 0L
        }
        if (!store.isConfigured) return
        // Status and Windows volume in parallel: a blocked helper used to stall the whole
        // poll (time and seek bar only updated every ~11s when VLC answered but the helper didn't).
        // While VLC is closed, re-check it every 4th cycle instead of every cycle (fewer
        // dead-end requests); the helper ping still runs every cycle either way.
        val skipVlc = vlcClosed && (closedPolls++ % CmdPolicy.CLOSED_POLL_EVERY != 0)
        // One locked read is this poll's target: the VLC status call, the helper call and any heal of a refusal all refer to this PC.
        val pollSnap = withContext(Dispatchers.IO) { store.helperVlcSnapshot() }   // locked read stays off Main
        val vlcDeferred = if (skipVlc) null
            else scope.async { twiceIO { Vlc.status(pollSnap.endpoint.host, pollSnap.vlcPort, pollSnap.vlcPassword) } }
        val volEp = pollSnap.endpoint
        val volDeferred = if (volEp.token.isNotBlank())
            scope.async {
                try { twiceIO { HelperClient.volumeGet(volEp.host, volEp.port, volEp.token) } }
                catch (e: HelperClient.BadToken) { throw e }
                catch (e: HelperClient.KeyChanged) { throw e }
                catch (e: Exception) { null }
            }
        else null
        // The helper's answer counts even when VLC's does not: the volume reading used to
        // be thrown away on the VLC-failure path, leaving the row stuck at its last value.
        if (volDeferred != null) {
            keyChanged = KeyChangeState.isChanged(HelperClient.pairingSource()?.pcId)   // recomputed from shared state, never cleared by a good poll
            val vm = try { volDeferred.await() } catch (e: HelperClient.BadToken) { if (samePollPc(pollSnap)) unpaired = true; null } catch (e: HelperClient.KeyChanged) { if (samePollPc(pollSnap)) keyChanged = true; null }
            // Every helper outcome below describes the PC this poll targeted; if the pairing changed meanwhile none of it is published.
            if (!samePollPc(pollSnap)) return
            if (vm != null) {
                helperFailures = 0
                if (System.currentTimeMillis() - localMuteAt > 1500) muted = vm.second
                helperDown = false
                helperDownAt = 0L
                helperNotice = false
                // The "moved" notice waits for this: a poll that just authenticated against the stored record. A saved address alone is
                // not a working session. It stays ~10 s after that first success so it can be read, then clears.
                helperMovedTo = movedGate.onAuthenticatedPoll(System.currentTimeMillis(), HelperRediscover.addressKey(volEp.host, volEp.port) + "|" + volEp.token)
                unpaired = false
                // Trust the phone's own taps briefly: the poll doesn't overwrite a volume
                // change that is still landing (that overwrite made the number jump back).
                if (System.currentTimeMillis() - localVolumeAt > 1500) winVolume = vm.first
            } else {
                // One blip keeps the last reading; two in a row means the helper is really gone.
                helperFailures++
                if (helperFailures >= 2) {
                    if (!helperDown) helperDownAt = System.currentTimeMillis()
                    helperDown = true; winVolume = null
                }
                helperNotice = helperDown && !unpaired &&
                    System.currentTimeMillis() - helperDownAt >= 5000
            }
        } else winVolume = null
        // A cached VLC-closed state must not mask a subsequent loss of the PC.
        // Helper liveness is checked on every poll, including the skipped VLC cycles.
        if (helperDown && vlcClosed) vlcClosed = false
        if (skipVlc) return
        try {
            val s = vlcDeferred!!.await()
            if (store.selectedSource != Source.VLC) return
            if (!samePollPc(pollSnap)) return   // answered by the PC this poll targeted, but a re-pair landed since: do not describe the new PC
            status = s
            lastConnectedAt = android.os.SystemClock.elapsedRealtime()
            // A new video clears the remembered subtitle pick; the sheet then shows the
            // file's own selected track instead of a checkmark from the previous video.
            if (s.title != lastTitle || s.lengthSec != lastLengthSec) {
                lastTitle = s.title; lastLengthSec = s.lengthSec
                subtitleRequest++
                subtitleListRequest++
                subtitleListLoading = false
                subtitleMutationLoading = false
                pickedSubtitle = null
                pickedSubtitleFile = null
                subtitleFolder = null
            }
            failures = 0
            unreachable = false
            vlcRefused = false
            vlcClosed = false
            closedPolls = 0
            connecting = false
            expectingVlc = false
        } catch (e: Vlc.BadAuth) {
            if (!samePollPc(pollSnap)) return
            lastConnectedAt = 0L
            // The one automatic heal: adopt the PC's current password once. The phone never
            // pushes its stored copy at the PC - a refusal can be an intentional PC change.
            val healed = withContext(Dispatchers.IO) { VlcAuthRepair.tryHealOnce(store, pollSnap.endpoint.token) }
            if (!samePollPc(pollSnap)) return   // guard before ANY write, including clearing the refusal
            if (healed) { vlcRefused = false; return } // the next poll uses the fresh password
            vlcRefused = true
            failures = UNREACHABLE_AFTER
            unreachable = true
        } catch (e: Exception) {
            // A failure of a poll against a PC that has since been replaced says nothing about the new pairing: drop it.
            if (!samePollPc(pollSnap)) return
            // The PC answering its helper but not VLC means VLC simply isn't open. Asked of the POLLED endpoint, before the guarded block below.
            val pingEp = pollSnap.endpoint
            val closed = failures + 1 >= UNREACHABLE_AFTER && pingEp.token.isNotBlank() &&
                withContext(Dispatchers.IO) {
                    HelperClient.ping(pingEp.host, pingEp.port, pingEp.token) ||
                        HelperClient.ping(pingEp.host, pingEp.port, pingEp.token)
                }
            // From here to the end: one non-suspending Main block. Guard first, then every write.
            if (!samePollPc(pollSnap)) return
            lastConnectedAt = 0L
            failures++
            if (failures >= UNREACHABLE_AFTER) {
                unreachable = true
                vlcClosed = closed
                rediscover() // always on since 1.9 (the toggle is gone: nobody had a reason to shut it off)
            }
        }
    }

    /** The laptop's IP changes with DHCP: ask the network for the helper. S11b
     *  (security-settled): the saved helper token is NEVER sent to a discovered
     *  host - the ping already discloses it, and the VLC password would follow.
     *  Only the pairing-learned SAVED endpoint may see the token; a discovered
     *  helper earns a notice, and re-pairing is the only safe way to move.
     *  VLC path only - never broadcast while Zephyr is selected or VLC was
     *  never set up (teardown A1). */
    fun rediscover() {
        if (store.selectedSource != Source.VLC || store.helperToken.isBlank()) return
        // Same 60 s backoff as the Zephyr path: a dead PC must not mean a broadcast every failed poll (round-4b).
        // Sole release point is the job's completion (RediscoverAdmission.launchIfAdmitted), so a cancelled-before-start job cannot wedge it.
        helperAdmission.launchIfAdmitted(scope, Dispatchers.IO) {
            // Saved endpoint first; discovered hosts get only a tokenless handshake against the STORED key, and only a
            // key match can move the stored address (HelperMove). The token never goes to a discovered host.
            when (val out = HelperMove.attempt(
                store.securePairStore,
                pingSaved = { c -> HelperClient.ping(c.host, c.port, c.token) },
                discover = { HelperClient.discover(3000) },
            )) {
                is HelperMove.Outcome.Moved -> withContext(Dispatchers.Main) {
                    // One non-suspending Main block: re-read the stored record and publish it only if it is still the moved one.
                    // A re-pair that landed meanwhile publishes its own record after this block.
                    if (publishIfCurrent(out.credential.encode())) {
                        failures = 0
                        // Notice only after a confirmed commit AND the next successful authenticated poll (see the poll success branch).
                        if (out.committed) movedGate.onDurableMove(out.pcName.ifBlank { out.credential.host }, HelperRediscover.addressKey(out.credential.host, out.credential.port) + "|" + out.credential.token, System.currentTimeMillis())
                    }
                }
                HelperMove.Outcome.SavedEndpointSeen -> withContext(Dispatchers.Main) { failures = 0 }
                else -> { /* keep polling; a Blocked store already shows "pair again" through helperPairing() */ }
            }
        }
    }

    /** A helper BadToken seen outside the command loop (e.g. starting a movie from the
     *  Movies screen): the PC removed this phone's pairing - route to pair-again,
     *  never to a generic failure (round-8 N-5). */
    fun onHelperBadToken() {
        PairingStatePolicy.onBadToken().also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures }
    }

    fun retry() {
        failures = 0
        connecting = true
        scope.launch { pollOnce() }
    }

    /** Set when the phone itself changes Windows volume; the poll trusts the phone for 1.5s. */
    private var localVolumeAt = 0L
    /** Same 1.5s trust window for mute taps (teardown C13). */
    private var localMuteAt = 0L

    // One ordered queue for playback/volume commands: rapid volume taps and the phone's
    // volume keys land in tap order instead of racing each other in parallel coroutines.
    // Taps carry their time: a play/pause/skip/seek queued more than about 2 s (a dead
    // PC, a stalled request) is a tap he no longer expects, so it is dropped - and a
    // burst of volume steps folds into the newest one.
    /** PC token of the command now running (null for a non-VLC command). Written and read only by the single command-executor coroutine. */
    @Volatile private var runTok: Store.HelperVlcSnapshot? = null
    /** Main thread, no suspension: true unless the running command's PC is no longer the stored pairing. */
    private fun cmdPcCurrent(): Boolean =
        runTok.let { it == null || it.samePc(store.pairingPeek()) } && (runZ?.same(store.zephyrTarget()) ?: true)

    private val cmds = CommandQueue(
        currentSubtitleGeneration = { subtitleRequest },
        onVolumeCoalesced = { Diag.volumeCoalesced.incrementAndGet() },
        onExpired = { Diag.cmdsExpired.incrementAndGet(); if (it.kind == "sub") scope.launch { subtitleMutationLoading = false } },
    )
    init {
        scope.launch(Dispatchers.IO) {
            while (isActive) {
                val c = cmds.next()
                runTok = null; runZ = null
                try { c.block() }
                // Outcome writes run as one non-suspending Main block that first checks the command's PC is still the stored pairing.
                catch (e: ZephyrClient.BadToken) {
                    // A revoked pairing is not a PC outage (N2): say "pair again".
                    withContext(Dispatchers.Main) { if (cmdPcCurrent()) PairingStatePolicy.onBadToken().also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures } }
                }
                catch (e: HelperClient.BadToken) {
                    // S-4: a revoked HELPER pairing is not a PC outage either.
                    withContext(Dispatchers.Main) { if (cmdPcCurrent()) PairingStatePolicy.onBadToken().also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures } }
                }
                catch (e: HelperClient.KeyChanged) { withContext(Dispatchers.Main) { if (cmdPcCurrent()) keyChanged = true } }
                catch (e: Exception) {
                    if (e is kotlinx.coroutines.CancellationException) throw e
                    // A command the pin refused was never sent: not a PC failure.
                    if (e is ZephyrClient.PairingChanged || e is ZephyrClient.PinUnavailable) continue
                    withContext(Dispatchers.Main) {
                        if (cmdPcCurrent()) PairingStatePolicy.onFailure(PairingStatePolicy.Flags(unpaired, unreachable, failures), UNREACHABLE_AFTER)
                            .also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures }
                    }
                }
            }
        }
    }

    /** Every non-subtitle command. Subtitle commands must go through [fireSubtitle], which requires
     *  the generation, so a missed caller cannot silently default to a value that is always dropped. */
    private fun fire(kind: String, block: (Store.HelperVlcSnapshot) -> Unit) {
        require(kind != "sub") { "subtitle commands must use fireSubtitle(generation)" }
        enqueue(kind, NO_GENERATION, block)
    }
    private fun fireSubtitle(generation: Int, block: (Store.HelperVlcSnapshot) -> Unit) = enqueue("sub", generation, block)
    private fun enqueue(kind: String, subtitleGeneration: Int, block: (Store.HelperVlcSnapshot) -> Unit) {
        // An unpaired source drops taps quietly - they are not PC failures
        // and must not feed the unreachable counter.
        if (!store.isPaired(store.selectedSource)) return
        val target = store.selectedSource
        // Same for the Zephyr Player: the whole tuple at tap time.
        val zTap = if (target == Source.ZEPHYR) store.zephyrTarget() else null
        // The PC this tap was made for (pairing token; lock-free read). A command that runs after a re-pair does not follow the new PC.
        val tapPeek = if (target == Source.VLC) store.pairingPeek() else null
        if (target == Source.VLC && tapPeek == null) return   // pairing unloaded or being written: not a current PC, drop the tap quietly (not a PC failure)
        val tapTok = tapPeek   // the whole captured tuple (token, VLC port, VLC password); an address move keeps it
        // First press while the PC's screen is dark only wakes it (VLC source; helper-routed
        // ones are also swallowed PC-side, so one tap never half-lands). A queued tap never
        // moves to a different source after a selector change.
        val gated: () -> Unit = {
            if (store.selectedSource == target && store.isPaired(target)) {
                // ONE locked read for the whole command (dark-screen probe, wake and the block itself), taken at execution on this IO thread.
                val snap = store.helperVlcSnapshot()
                val ep = snap.endpoint
                val zNow = if (target == Source.ZEPHYR) store.zephyrTarget() else null
                val zOk = target != Source.ZEPHYR || (zTap != null && zTap.same(zNow))
                if ((tapTok == null || tapTok.samePc(snap)) && zOk) {   // else re-paired since the tap: drop it, not a PC failure
                    runTok = tapTok
                    runZ = zTap
                    if (target == Source.VLC && HelperClient.screenIsDark(ep.host, ep.port, ep.token)) {
                        HelperClient.wakeDisplay(ep.host, ep.port, ep.token)
                    } else {
                        block(snap)
                    }
                }
            }
        }
        cmds.trySend(Cmd(kind, System.currentTimeMillis(), subtitleGeneration, gated))
    }

    fun togglePause() {
        // A disconnected tap is intentionally inert, without disabled styling.
        if (unreachable || vlcClosed || vlcRefused || lastConnectedAt == 0L ||
            android.os.SystemClock.elapsedRealtime() - lastConnectedAt > 5000) return
        if (BuildConfig.DEBUG) Log.i("BMSeek", "togglePause")
        // Flip the icon at once; the next status poll (about a second out) confirms or corrects it.
        val s = status
        if (s != null) {
            status = when (s.state.lowercase()) {
                "playing" -> s.copy(state = "paused")
                "paused" -> s.copy(state = "playing")
                else -> s
            }
        }
        fire("playpause") { snap ->
            if (store.selectedSource == Source.ZEPHYR) {
                if (s?.playing == true) ZephyrClient.pause(zt())
                else ZephyrClient.resume(zt())
            } else snap.withVlc { h, p, w -> Vlc.togglePause(h, p, w) }
        }
    }
    fun toggleFullscreen() = fire("fullscreen") { snap ->
        if (store.selectedSource == Source.ZEPHYR)
            ZephyrClient.fullscreen(zt(), !(status?.fullscreen ?: false))
        else snap.withVlc { h, p, w -> Vlc.toggleFullscreen(h, p, w) }
    }
    fun skip(seconds: Int) {
        val current = status
        if (store.selectedSource == Source.ZEPHYR && current == null) return
        var zephyrTarget = 0L
        if (store.selectedSource == Source.ZEPHYR) {
            // Length 0/unknown must not pin the target to 0 (teardown B2).
            val raw = current!!.timeSec + seconds
            zephyrTarget = if (current.lengthSec > 0) raw.coerceIn(0, current.lengthSec) else raw.coerceAtLeast(0)
            // Echo the tap locally so rapid taps accumulate instead of all seeking to
            // the same point; the poll corrects within about a second (teardown B1).
            status = current.copy(timeSec = zephyrTarget)
        }
        fire("skip") { snap ->
            if (store.selectedSource == Source.ZEPHYR)
                ZephyrClient.seek(zt(), zephyrTarget)
            else snap.withVlc { h, p, w -> Vlc.skip(h, p, w, seconds) }
        }
    }
    fun seekTo(seconds: Long) {
        if (BuildConfig.DEBUG) Log.i("BMSeek", "seekTo seconds=$seconds")
        fire("seek") { snap ->
            if (store.selectedSource == Source.ZEPHYR) ZephyrClient.seek(zt(), seconds)
            else snap.withVlc { h, p, w -> Vlc.seekTo(h, p, w, seconds) }
        }
    }

    fun volumeStep(up: Boolean) {
        if (store.selectedSource == Source.ZEPHYR) {
            val current = status ?: return
            val next = (current.volumePct + if (up) 5 else -5).coerceIn(0, 100)
            status = status?.copy(volumeRaw = next * 256 / 100)
            localVolumeAt = System.currentTimeMillis()
            val wasMuted = muted; muted = false
            if (wasMuted) {
                localMuteAt = System.currentTimeMillis()
                // Its own "mute" command: folded into "volume", two quick volume taps
                // would coalesce the unmute away and the PC stays muted (S6).
                fire("mute") { snap -> ZephyrClient.mute(zt(), false) }
            }
            fire("volume") { snap ->
                ZephyrClient.volume(zt(), next)
            }
            return
        }
        val win = winVolume
        if (win != null) {
            // Windows system volume via the helper.
            val base = win
            val wasMuted = muted
            if (wasMuted) muted = false
            val next = (base + if (up) 5 else -5).coerceIn(0, 100)
            winVolume = next
            localVolumeAt = System.currentTimeMillis()
            fire("volume") { snap ->
                val ep = snap.endpoint
                HelperClient.volumeSet(ep.host, ep.port, ep.token, next)
                // Unmute only when it WAS muted - a plain volume step must not touch the mute switch.
                if (wasMuted) HelperClient.volumeMute(ep.host, ep.port, ep.token, false)
            }
            return
        }
        val s = status
        if (muted) {
            // Restore-then-step: unmute to where we were, then one step from there.
            val base = preMuteRaw
            muted = false
            val next = (base + if (up) Vlc.VOLUME_STEP else -Vlc.VOLUME_STEP).coerceIn(0, Vlc.VOLUME_100)
            status?.let { status = it.copy(volumeRaw = next) }
            fire("volume") { snap -> snap.withVlc { h, p, w -> Vlc.setVolume(h, p, w, next) } }
        } else {
            val cur = s?.volumeRaw ?: return
            val next = (cur + if (up) Vlc.VOLUME_STEP else -Vlc.VOLUME_STEP).coerceIn(0, Vlc.VOLUME_100)
            // Echo the tap on the row right away - with the PC unreachable the poll can't
            // do it, and a tap that changes nothing feels dead. The next good poll overwrites.
            status = s.copy(volumeRaw = next)
            fire("volume") { snap -> snap.withVlc { h, p, w -> Vlc.setVolume(h, p, w, next) } }
        }
    }

    fun toggleMute() {
        if (store.selectedSource == Source.ZEPHYR) {
            val next = !muted; muted = next
            localMuteAt = System.currentTimeMillis()
            // Mute gets its own queue kind: folding it into "volume" could drop volume
            // steps queued just before it (teardown C13).
            fire("mute") { snap -> ZephyrClient.mute(zt(), next) }
            return
        }
        if (winVolume != null) {
            val next = !muted
            muted = next
            localMuteAt = System.currentTimeMillis()
            fire("mute") { snap -> snap.withHelper { h, p, t -> HelperClient.volumeMute(h, p, t, next) } }
            return
        }
        if (muted) {
            muted = false
            val back = preMuteRaw.coerceIn(1, Vlc.VOLUME_100)
            fire("volume") { snap -> snap.withVlc { h, p, w -> Vlc.setVolume(h, p, w, back) } }
        } else {
            preMuteRaw = (status?.volumeRaw ?: Vlc.VOLUME_100 / 2).coerceIn(1, Vlc.VOLUME_100 * 2)
            muted = true
            fire("volume") { snap -> snap.withVlc { h, p, w -> Vlc.setVolume(h, p, w, 0) } }
        }
    }

    var subtitleFolder by mutableStateOf<HelperClient.SubtitleFolder?>(null); private set
    private var subtitleListLoading by mutableStateOf(false)
    private var subtitleMutationLoading by mutableStateOf(false)
    val subtitleFilesLoading get() = subtitleListLoading || subtitleMutationLoading
    private var subtitleListError by mutableStateOf<String?>(null)
    private var subtitleMutationError by mutableStateOf<String?>(null)
    val subtitleFileError get() = subtitleMutationError ?: subtitleListError
    private var confirmedSubtitleMedia: String? = null
    private val subtitleJournal = SubtitleJournal(app)
    private val subtitleWarningKey: String get() = java.security.MessageDigest.getInstance("SHA-256")
        .digest(("subtitle-v1:" + store.helperToken).toByteArray(Charsets.UTF_8))
        .joinToString("") { "%02x".format(it) }
    var subtitleOutcomeUnknown by mutableStateOf(false); private set
    private var uncertainFolderMedia: String? = null
    private var uncertainSubtitleMedia: Set<String> = emptySet()
    private var loadedWarningKey: String? = null
    private var warningReadInFlight = false
    private var warningReadAgain = false
    @Volatile private var warningGeneration = 0
    private suspend fun readSubtitleWarnings(key:String,current:String?): Pair<List<SubtitleJournal.Entry>,Boolean> = withContext(Dispatchers.IO){
        try{
            subtitleJournal.entries(key) to subtitleJournal.isDegraded(key)
        }catch(_:Exception){emptyList<SubtitleJournal.Entry>() to true}
    }
    private fun applySubtitleWarnings(key:String,current:String?,result:Pair<List<SubtitleJournal.Entry>,Boolean>): Boolean {
        if(key!=subtitleWarningKey || current!=confirmedSubtitleMedia)return false
        val records=result.first
        val relevant=records.filter{current.isNullOrEmpty() || it.media.isEmpty() || it.media==current || it.phase=="pending"}
        val wasUnknown=subtitleOutcomeUnknown
        subtitleOutcomeUnknown=result.second || relevant.isNotEmpty()
        uncertainSubtitleMedia=relevant.map{it.media}.toSet()
        uncertainFolderMedia=relevant.firstOrNull{it.media==current}?.media ?: relevant.firstOrNull()?.media
        if(result.second)subtitleMutationError="Subtitle safety history couldn't be read. New file loads are paused because their previous outcome cannot be established. Existing controls remain best-effort."
        else if(subtitleOutcomeUnknown)subtitleMutationError="Couldn't confirm the previous subtitle change. Existing controls are still available."
        else if(wasUnknown)subtitleMutationError=null
        return true
    }
    private fun reconcileSubtitleWarnings() {
        val key=subtitleWarningKey
        if(loadedWarningKey!=key){
            loadedWarningKey=key;confirmedSubtitleMedia=null;pickedSubtitle=null;pickedSubtitleFile=null
            subtitleFolder=null;subtitleRequest++;subtitleListRequest++;warningGeneration++
            subtitleListLoading=false;subtitleMutationLoading=false;subtitleMutationError=null
            subtitleOutcomeUnknown=false;uncertainFolderMedia=null;uncertainSubtitleMedia=emptySet()
        }
        if(warningReadInFlight){warningReadAgain=true;return}
        warningReadInFlight=true
        scope.launch {
            try {
                do {
                    warningReadAgain=false
                    val readKey=subtitleWarningKey;val current=confirmedSubtitleMedia
                    val generation=warningGeneration;val mutation=subtitleRequest
                    val result=readSubtitleWarnings(readKey,current)
                    if(generation==warningGeneration && mutation==subtitleRequest)applySubtitleWarnings(readKey,current,result)
                    else warningReadAgain=true
                } while(warningReadAgain)
            } finally { warningReadInFlight=false }
        }
    }
    private fun beginSubtitleMutation(media: String?, key: String, folderLoad:Boolean=false): String = try { subtitleJournal.begin(key,media,folderLoad) }
        catch (_: Exception) { throw HelperClient.SubtitleRejected("Couldn't save subtitle safety state. Try again.") }
    private fun finishSubtitleMutation(id: String?, unknown: Boolean) {
        if (id != null) { subtitleJournal.finish(id,unknown);warningGeneration++ }
    }
    var pickedSubtitleFile by mutableStateOf<String?>(null); private set
    @Volatile private var subtitleRequest = 0
    private var subtitleListRequest = 0
    private var subtitleListInFlight = false
    init { reconcileSubtitleWarnings() }

    internal fun debugSubtitlePreview() {
        if (!BuildConfig.DEBUG) return
        status = VlcStatus("playing", "Demo", 30, 3600, 128, false,
            listOf(SubTrack(1, "English", false), SubTrack(2, "Spanish", false)))
        subtitleFolder = HelperClient.SubtitleFolder("demo", listOf(
            HelperClient.SubtitleFile("demo-en", "Movie.en.srt"),
            HelperClient.SubtitleFile("demo-es", "Movie.es.srt")), false)
        pickedSubtitleFile = "demo-en"
    }
    /** Screenshot-rig only: seeds the leftover list message a prior unavailable/
     *  truncated result would have set, so tests can prove a later failed check
     *  clears it. Debug builds only. */
    internal fun debugSeedSubtitleListError() {
        if (!BuildConfig.DEBUG) return
        subtitleListError = "Can't check subtitle files right now."
    }
    fun refreshSubtitleFiles(quiet: Boolean = false) {
        if (store.selectedSource != Source.VLC) return
        reconcileSubtitleWarnings()
        if (BuildConfig.DEBUG && subtitleFolder?.mediaId == "demo") return
        if (subtitleListInFlight) return
        subtitleListInFlight = true
        val request = ++subtitleListRequest
        val mutationAtStart = subtitleRequest
        if (!quiet) { subtitleListLoading = true; subtitleListError = null }
        val ep = store.helperEndpoint(); val host = ep.host; val port = ep.port; val token = ep.token
        scope.launch {
            try {
                val folder = withContext(Dispatchers.IO) { HelperClient.subtitleFiles(host, port, token) }
                if (request == subtitleListRequest && mutationAtStart == subtitleRequest) {
                    if (!folder.unavailable && folder.mediaId.isNotEmpty() && confirmedSubtitleMedia != folder.mediaId) { pickedSubtitleFile = null; pickedSubtitle = null }
                    if (!folder.unavailable && folder.mediaId.isNotEmpty()) {
                        confirmedSubtitleMedia = folder.mediaId
                        reconcileSubtitleWarnings()
                    }
                    subtitleFolder = folder
                    subtitleListError = null
                    if (folder.unavailable) subtitleListError = "Can't check subtitle files right now."
                    else if (folder.truncated) subtitleListError = "This folder is large. Only some subtitle files are shown."
                }
            } catch (e: kotlinx.coroutines.CancellationException) { throw e
            } catch (e: Exception) {
                if (request == subtitleListRequest && mutationAtStart == subtitleRequest) {
                    // No connection to the PC (or an older PC app without folder
                    // subtitles): folder entries simply stay hidden, exactly like
                    // before the feature existed. No error line, no Retry. A leftover
                    // message from an earlier unavailable/truncated result is stale
                    // content and must go too, or it would render an error/Retry the
                    // current failure did not produce.
                    subtitleFolder = null
                    // Quiet only when the PC is gone (raw socket failure), too old
                    // (MissingRoute), or unpaired (BadToken - the main screen owns
                    // that notice). An ANSWERING helper whose check failed keeps an
                    // honest message + Retry, same wording as its unavailable answer.
                    subtitleListError = when (e) {
                        is HelperClient.MissingRoute, is HelperClient.BadToken, is java.io.IOException -> null
                        else -> "Can't check subtitle files right now."
                    }
                }
            } finally { subtitleListInFlight = false; if (request == subtitleListRequest) subtitleListLoading = false }
        }
    }
    fun loadSubtitleFile(file: HelperClient.SubtitleFile) {
        if (store.selectedSource != Source.VLC) return
        reconcileSubtitleWarnings()
        val folder = subtitleFolder ?: return
        if (folder.mediaId in uncertainSubtitleMedia || "" in uncertainSubtitleMedia) {
            subtitleMutationError = "Couldn't confirm the file load. Existing subtitle controls are still available."
            return
        }
        val request = ++subtitleRequest
        val ep = store.helperEndpoint(); val host = ep.host; val port = ep.port; val token = ep.token
        val warningKey = subtitleWarningKey
        subtitleMutationError = null
        subtitleMutationLoading = true
        // Same ordered command lane as Off/loaded-track selection. A later Off cannot
        // overtake a slow add and then be undone when that add eventually lands.
        cmds.trySend(Cmd("sub", System.currentTimeMillis(), request) {
            var journalId: String? = null
            try {
                if (warningKey != subtitleWarningKey || store.selectedSource != Source.VLC) throw HelperClient.SubtitleRejected("pairing changed")
                journalId = beginSubtitleMutation(folder.mediaId,warningKey,true)
                val loaded = HelperClient.loadSubtitle(host, port, token, folder.mediaId, file.id)
                finishSubtitleMutation(journalId,false)
                val refreshed = if (loaded) try { HelperClient.subtitleFiles(host, port, token) } catch (_: Exception) { null } else null
                scope.launch {
                    if (request == subtitleRequest && host == store.host && token == store.helperToken) {
                        val current=confirmedSubtitleMedia
                        val result=readSubtitleWarnings(warningKey,current)
                        if(request!=subtitleRequest || warningKey!=subtitleWarningKey)return@launch
                        if(!applySubtitleWarnings(warningKey,current,result))return@launch
                        if (loaded) {
                            pickedSubtitle = null
                            pickedSubtitleFile = if (refreshed?.mediaId == folder.mediaId) file.id else null
                            subtitleFolder = refreshed
                            if (refreshed == null || refreshed.unavailable) subtitleMutationError = "Subtitle loaded. Couldn't refresh the list. Tap Retry."
                        } else subtitleMutationError = "Screen woken. Tap the subtitle again to load it."
                        subtitleMutationLoading = false
                    }
                }
            } catch (_: HelperClient.SubtitleRejected) {
                finishSubtitleMutation(journalId,false)
                scope.launch { if (warningKey == subtitleWarningKey && request == subtitleRequest) {
                    subtitleMutationError = "That subtitle selection changed. Refresh the list and try again."
                    subtitleMutationLoading = false
                } }
            } catch (_: Exception) {
                finishSubtitleMutation(journalId,true)
                scope.launch {
                    // A newer intent can hide an old success, but cannot erase an
                    // uncertain mutation that may already have reached VLC.
                    if (warningKey == subtitleWarningKey && (confirmedSubtitleMedia == null || confirmedSubtitleMedia == folder.mediaId)) {
                        subtitleOutcomeUnknown = true
                        uncertainFolderMedia = folder.mediaId
                        uncertainSubtitleMedia = uncertainSubtitleMedia + folder.mediaId
                        subtitleMutationError = "Couldn't confirm the file load. Existing subtitle controls are still available."
                    }
                    if (warningKey == subtitleWarningKey && request == subtitleRequest) subtitleMutationLoading = false
                }
            }
        })
    }

    fun pickSubtitle(trackId: Int) {
        if (store.selectedSource == Source.ZEPHYR) {
            pickedSubtitle = trackId
            fireSubtitle(subtitleRequest) { _ -> ZephyrClient.captions(zt(), trackId) }
            return
        }
        reconcileSubtitleWarnings()
        subtitleRequest++
        subtitleMutationLoading = false
        val request = subtitleRequest
        val snap = store.helperVlcSnapshot(); val ep = snap.endpoint; val host = ep.host; val port = ep.port; val token = ep.token
        val warningKey = subtitleWarningKey
        val vlcPort = snap.vlcPort; val password = snap.vlcPassword
        val mediaId = confirmedSubtitleMedia
        if (!subtitleOutcomeUnknown) subtitleMutationError = null
        subtitleMutationLoading = true
        cmds.trySend(Cmd("sub", System.currentTimeMillis(), request) {
            var journalId: String? = null
            var dispatched = false
            var dispatchedMedia: String? = null
            try {
                if (warningKey != subtitleWarningKey) throw HelperClient.SubtitleRejected("pairing changed")
                val currentMedia = try { HelperClient.subtitleMedia(host,port,token) } catch (_: HelperClient.MissingRoute) { null }
                if (trackId != -1 && mediaId != null && currentMedia != null && mediaId != currentMedia) throw HelperClient.SubtitleRejected("movie changed")
                dispatchedMedia = currentMedia ?: confirmedSubtitleMedia
                try { journalId = beginSubtitleMutation(dispatchedMedia,warningKey) }
                catch (_: HelperClient.SubtitleRejected) {
                    // Existing track controls remain usable when history storage is
                    // unavailable, but cannot claim a confirmed durable outcome.
                    subtitleJournal.markDegraded(warningKey)
                    scope.launch { if(warningKey==subtitleWarningKey){
                        subtitleOutcomeUnknown=true
                        subtitleMutationError="Subtitle history couldn't be saved. Existing controls are best-effort; the selected state is unconfirmed."
                    } }
                }
                dispatched = true
                if (currentMedia == null || !HelperClient.selectSubtitle(host, port, token, trackId, currentMedia)) {
                    if (HelperClient.screenIsDark(host, port, token)) {
                        HelperClient.wakeDisplay(host, port, token)
                        throw HelperClient.SubtitleRejected("wake only")
                    }
                    Vlc.setSubtitleTrack(host, vlcPort, password, trackId)
                }
                finishSubtitleMutation(journalId,false)
                scope.launch {
                    if (warningKey == subtitleWarningKey && request == subtitleRequest) {
                        val current=confirmedSubtitleMedia
                        val result=readSubtitleWarnings(warningKey,current)
                        if(request!=subtitleRequest || warningKey!=subtitleWarningKey)return@launch
                        if(!applySubtitleWarnings(warningKey,current,result))return@launch
                        pickedSubtitleFile=null;if(!subtitleOutcomeUnknown)pickedSubtitle=trackId
                        subtitleMutationLoading=false
                    }
                }
            } catch (_: HelperClient.SubtitleRejected) {
                finishSubtitleMutation(journalId,false)
                scope.launch { if (warningKey == subtitleWarningKey && request == subtitleRequest) {
                    subtitleMutationError = "The movie or subtitle selection changed. Try again."
                    subtitleMutationLoading = false
                } }
            } catch (_: Exception) {
                if (dispatched) finishSubtitleMutation(journalId,true)
                scope.launch {
                    if (warningKey == subtitleWarningKey && dispatched && (mediaId == null || confirmedSubtitleMedia == null || confirmedSubtitleMedia == mediaId)) {
                        subtitleOutcomeUnknown = true
                        uncertainFolderMedia = dispatchedMedia
                        uncertainSubtitleMedia = uncertainSubtitleMedia + dispatchedMedia.orEmpty()
                        subtitleMutationError = "Couldn't confirm the subtitle change. You can try the existing controls again."
                    }
                    if (warningKey == subtitleWarningKey && !dispatched && request == subtitleRequest) subtitleMutationError = "Can't reach the PC to change subtitles. Try again."
                    if (warningKey == subtitleWarningKey && request == subtitleRequest) subtitleMutationLoading = false
                }
            }
        })
    }
}

private const val NO_GENERATION = Int.MIN_VALUE
private typealias Cmd = CommandQueue.Cmd
