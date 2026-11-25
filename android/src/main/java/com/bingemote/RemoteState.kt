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
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
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

    /** Pairing, re-pairing or rediscovery learned an address: open screens reconnect to it. */
    fun publishAddress(host: String, port: Int, token: String) {
        liveHost = host; livePort = port; liveToken = token
    }

    /** Screenshot-rig only: a fake playing video so seek-drag captures show real times. */
    fun debugSeedStatus() {
        if (BuildConfig.DEBUG) status = VlcStatus("playing", "Demo", 1500, 6624, 128, false, emptyList())
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
    @Volatile private var rediscovering = false
    @Volatile private var helperLastRediscoverAt = 0L
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
        helperDown = false; helperNotice = false; helperMovedTo = null; unpaired = false
        failures = 0; connecting = true; unreachable = true
        retry()
    }

    // N-3: no fallback to the helper host - the Zephyr bearer only ever goes to
    // the address it was paired with. (Blank simply fails the poll while unpaired.)
    private fun zephyrHost() = store.zephyrHost

    private suspend fun pollZephyr() {
        if (store.zephyrToken.isBlank()) {
            unreachable = true; connecting = false; return
        }
        try {
            val json = withContext(Dispatchers.IO) {
                ZephyrClient.controlStatus(zephyrHost(), store.zephyrPort, store.zephyrToken)
            }
            if (store.selectedSource != Source.ZEPHYR) return
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
            zephyrMovedTo = null
            vlcClosed = false; vlcRefused = false; helperNotice = false; helperDown = false
        } catch (e: ZephyrClient.BadToken) {
            if (store.selectedSource != Source.ZEPHYR) return
            // The PC removed this phone's pairing - say "pair again", not "can't reach"
            // (N2: a 401 is NOT an outage, and must clear any outage already showing).
            connecting = false
            lastConnectedAt = 0L
            PairingStatePolicy.onBadToken().also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures }
        } catch (e: Exception) {
            if (store.selectedSource != Source.ZEPHYR) return
            PairingStatePolicy.onFailure(PairingStatePolicy.Flags(unpaired, unreachable, failures), UNREACHABLE_AFTER)
                .also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures }
            connecting = false
            // A DHCP address change looks like an outage: re-find the player and keep
            // the saved pairing (teardown C3). The token is the PC's identity.
            if (failures >= 2) zephyrRediscover()
        }
    }

    @Volatile private var zephyrRediscovering = false
    /** S11: set when the saved PC appears at a NEW address. The token never follows;
     *  the hero offers the safe re-pair path instead. Cleared on a good poll. */
    var zephyrMovedTo by mutableStateOf<String?>(null)
    private var zephyrLastRediscoverAt = 0L
    private fun zephyrRediscover() {
        if (zephyrRediscovering) return
        // Back off: rediscovering on every failing poll is a 3-6 s broadcast loop for
        // as long as the PC stays off (S11).
        val nowMs = System.currentTimeMillis()
        if (nowMs - zephyrLastRediscoverAt < 60_000) return
        zephyrLastRediscoverAt = nowMs
        zephyrRediscovering = true
        scope.launch(Dispatchers.IO) {
            try {
                // S11 (security): the saved bearer token is NEVER sent to a discovered
                // host - a spoofed same-name answer proves nothing. run() takes no
                // token and no HTTP client at all (ZephyrRediscover.kt).
                val plan = com.zephyr.remote.ZephyrRediscover.run(
                    store.zephyrHost, store.zephyrPort, store.zephyrPcName,
                    discover = { ZephyrClient.discoverPlayers(3000) })
                withContext(Dispatchers.Main) {
                    when (plan) {
                        is com.zephyr.remote.ZephyrRediscover.Plan.MovedNotice ->
                            zephyrMovedTo = plan.name // the hero says what to do; re-pair is the safe path
                        else -> { /* saved endpoint seen or nothing: the poll recovers on its own */ }
                    }
                }
            } finally {
                zephyrRediscovering = false
            }
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
    fun refreshPairing() {
        zephyrPaired = store.isPaired(Source.ZEPHYR)
        vlcPaired = store.isPaired(Source.VLC) || (BuildConfig.DEBUG && debugForcePaired)
    }
    fun pairedForSelected(): Boolean = if (store.selectedSource == Source.ZEPHYR) zephyrPaired else vlcPaired

    suspend fun pollOnce() {
        refreshPairing()
        if (store.selectedSource == Source.ZEPHYR) { pollZephyr(); return }
        // The helper-notice state belongs to the pairing it was learned from: a new
        // or cleared token makes it stale. Reset it here so a re-paired phone never
        // sees "pair again" (or a leftover helper warning) about its NEW pairing.
        // Runs before the isConfigured early-return so a cleared token also resets.
        if (noticeToken != store.helperToken) {
            noticeToken = store.helperToken
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
        val vlcDeferred = if (skipVlc) null
            else scope.async { twiceIO { Vlc.status(store.host, store.vlcPort, store.vlcPassword) } }
        val volDeferred = if (store.helperToken.isNotBlank())
            scope.async {
                try { twiceIO { HelperClient.volumeGet(store.host, store.helperPort, store.helperToken) } }
                catch (e: HelperClient.BadToken) { throw e }
                catch (e: Exception) { null }
            }
        else null
        // The helper's answer counts even when VLC's does not: the volume reading used to
        // be thrown away on the VLC-failure path, leaving the row stuck at its last value.
        if (volDeferred != null) {
            val vm = try { volDeferred.await() } catch (e: HelperClient.BadToken) { unpaired = true; null }
            if (vm != null) {
                helperFailures = 0
                if (System.currentTimeMillis() - localMuteAt > 1500) muted = vm.second
                helperDown = false
                helperDownAt = 0L
                helperNotice = false
                helperMovedTo = null
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
            lastConnectedAt = 0L
            // The one automatic heal: adopt the PC's current password once. The phone never
            // pushes its stored copy at the PC - a refusal can be an intentional PC change.
            val healed = withContext(Dispatchers.IO) { VlcAuthRepair.tryHealOnce(store) }
            if (healed) { vlcRefused = false; return } // the next poll uses the fresh password
            vlcRefused = true
            failures = UNREACHABLE_AFTER
            unreachable = true
        } catch (e: Exception) {
            lastConnectedAt = 0L
            failures++
            if (failures >= UNREACHABLE_AFTER) {
                unreachable = true
                // The PC answering its helper but not VLC means VLC simply isn't open.
                vlcClosed = store.helperToken.isNotBlank() &&
                    withContext(Dispatchers.IO) {
                        HelperClient.ping(store.host, store.helperPort, store.helperToken) ||
                            HelperClient.ping(store.host, store.helperPort, store.helperToken)
                    }
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
        if (rediscovering) return
        // Same 60 s backoff as the Zephyr path: a dead PC must not mean a
        // broadcast every failed poll (round-4b).
        val nowMs = System.currentTimeMillis()
        if (nowMs - helperLastRediscoverAt < 60_000) return
        helperLastRediscoverAt = nowMs
        rediscovering = true
        scope.launch(Dispatchers.IO) {
            try {
                // run() invokes the token-carrying ping ONLY against the saved,
                // pairing-learned endpoint; a discovered host is never probed
                // (S11b regression-tested at the HTTP level).
                when (val plan = HelperRediscover.run(
                    store.host, store.helperPort, store.pcName,
                    pingSaved = { HelperClient.ping(store.host, store.helperPort, store.helperToken) },
                    discover = { HelperClient.discover(3000) },
                )) {
                    is HelperRediscover.Plan.MovedNotice -> withContext(Dispatchers.Main) {
                        helperMovedTo = plan.name.ifBlank { plan.host }
                    }
                    HelperRediscover.Plan.SavedEndpointSeen -> withContext(Dispatchers.Main) { failures = 0 }
                    HelperRediscover.Plan.None -> { /* keep polling the saved endpoint */ }
                }
            } finally {
                rediscovering = false
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
    private val cmds = CommandQueue(
        currentSubtitleGeneration = { subtitleRequest },
        onVolumeCoalesced = { Diag.volumeCoalesced.incrementAndGet() },
        onExpired = { Diag.cmdsExpired.incrementAndGet(); if (it.kind == "sub") scope.launch { subtitleMutationLoading = false } },
    )
    init {
        scope.launch(Dispatchers.IO) {
            while (isActive) {
                val c = cmds.next()
                try { c.block() }
                catch (e: ZephyrClient.BadToken) {
                    // A revoked pairing is not a PC outage (N2): say "pair again".
                    PairingStatePolicy.onBadToken().also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures }
                }
                catch (e: HelperClient.BadToken) {
                    // S-4: a revoked HELPER pairing is not a PC outage either.
                    PairingStatePolicy.onBadToken().also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures }
                }
                catch (_: Exception) {
                    PairingStatePolicy.onFailure(PairingStatePolicy.Flags(unpaired, unreachable, failures), UNREACHABLE_AFTER)
                        .also { unpaired = it.unpaired; unreachable = it.unreachable; failures = it.failures }
                }
            }
        }
    }

    /** Every non-subtitle command. Subtitle commands must go through [fireSubtitle], which requires
     *  the generation, so a missed caller cannot silently default to a value that is always dropped. */
    private fun fire(kind: String, block: () -> Unit) {
        require(kind != "sub") { "subtitle commands must use fireSubtitle(generation)" }
        enqueue(kind, NO_GENERATION, block)
    }
    private fun fireSubtitle(generation: Int, block: () -> Unit) = enqueue("sub", generation, block)
    private fun enqueue(kind: String, subtitleGeneration: Int, block: () -> Unit) {
        // An unpaired source drops taps quietly - they are not PC failures
        // and must not feed the unreachable counter.
        if (!store.isPaired(store.selectedSource)) return
        val target = store.selectedSource
        // First press while the PC's screen is dark only wakes it (VLC source; helper-routed
        // ones are also swallowed PC-side, so one tap never half-lands). A queued tap never
        // moves to a different source after a selector change.
        val gated: () -> Unit = {
            if (store.selectedSource == target && store.isPaired(target)) {
                if (target == Source.VLC && HelperClient.screenIsDark(store.host, store.helperPort, store.helperToken)) {
                    HelperClient.wakeDisplay(store.host, store.helperPort, store.helperToken)
                } else {
                    block()
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
        fire("playpause") {
            if (store.selectedSource == Source.ZEPHYR) {
                if (s?.playing == true) ZephyrClient.pause(zephyrHost(), store.zephyrPort, store.zephyrToken)
                else ZephyrClient.resume(zephyrHost(), store.zephyrPort, store.zephyrToken)
            } else Vlc.togglePause(store.host, store.vlcPort, store.vlcPassword)
        }
    }
    fun toggleFullscreen() = fire("fullscreen") {
        if (store.selectedSource == Source.ZEPHYR)
            ZephyrClient.fullscreen(zephyrHost(), store.zephyrPort, store.zephyrToken, !(status?.fullscreen ?: false))
        else Vlc.toggleFullscreen(store.host, store.vlcPort, store.vlcPassword)
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
        fire("skip") {
            if (store.selectedSource == Source.ZEPHYR)
                ZephyrClient.seek(zephyrHost(), store.zephyrPort, store.zephyrToken, zephyrTarget)
            else Vlc.skip(store.host, store.vlcPort, store.vlcPassword, seconds)
        }
    }
    fun seekTo(seconds: Long) {
        if (BuildConfig.DEBUG) Log.i("BMSeek", "seekTo seconds=$seconds")
        fire("seek") {
            if (store.selectedSource == Source.ZEPHYR) ZephyrClient.seek(zephyrHost(), store.zephyrPort, store.zephyrToken, seconds)
            else Vlc.seekTo(store.host, store.vlcPort, store.vlcPassword, seconds)
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
                fire("mute") { ZephyrClient.mute(zephyrHost(), store.zephyrPort, store.zephyrToken, false) }
            }
            fire("volume") {
                ZephyrClient.volume(zephyrHost(), store.zephyrPort, store.zephyrToken, next)
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
            fire("volume") {
                HelperClient.volumeSet(store.host, store.helperPort, store.helperToken, next)
                // Unmute only when it WAS muted - a plain volume step must not touch the mute switch.
                if (wasMuted) HelperClient.volumeMute(store.host, store.helperPort, store.helperToken, false)
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
            fire("volume") { Vlc.setVolume(store.host, store.vlcPort, store.vlcPassword, next) }
        } else {
            val cur = s?.volumeRaw ?: return
            val next = (cur + if (up) Vlc.VOLUME_STEP else -Vlc.VOLUME_STEP).coerceIn(0, Vlc.VOLUME_100)
            // Echo the tap on the row right away - with the PC unreachable the poll can't
            // do it, and a tap that changes nothing feels dead. The next good poll overwrites.
            status = s.copy(volumeRaw = next)
            fire("volume") { Vlc.setVolume(store.host, store.vlcPort, store.vlcPassword, next) }
        }
    }

    fun toggleMute() {
        if (store.selectedSource == Source.ZEPHYR) {
            val next = !muted; muted = next
            localMuteAt = System.currentTimeMillis()
            // Mute gets its own queue kind: folding it into "volume" could drop volume
            // steps queued just before it (teardown C13).
            fire("mute") { ZephyrClient.mute(zephyrHost(), store.zephyrPort, store.zephyrToken, next) }
            return
        }
        if (winVolume != null) {
            val next = !muted
            muted = next
            localMuteAt = System.currentTimeMillis()
            fire("mute") { HelperClient.volumeMute(store.host, store.helperPort, store.helperToken, next) }
            return
        }
        if (muted) {
            muted = false
            val back = preMuteRaw.coerceIn(1, Vlc.VOLUME_100)
            fire("volume") { Vlc.setVolume(store.host, store.vlcPort, store.vlcPassword, back) }
        } else {
            preMuteRaw = (status?.volumeRaw ?: Vlc.VOLUME_100 / 2).coerceIn(1, Vlc.VOLUME_100 * 2)
            muted = true
            fire("volume") { Vlc.setVolume(store.host, store.vlcPort, store.vlcPassword, 0) }
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
        val host = store.host; val port = store.helperPort; val token = store.helperToken
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
        val host = store.host; val port = store.helperPort; val token = store.helperToken
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
            fireSubtitle(subtitleRequest) { ZephyrClient.captions(zephyrHost(), store.zephyrPort, store.zephyrToken, trackId) }
            return
        }
        reconcileSubtitleWarnings()
        subtitleRequest++
        subtitleMutationLoading = false
        val request = subtitleRequest
        val host = store.host; val port = store.helperPort; val token = store.helperToken
        val warningKey = subtitleWarningKey
        val vlcPort = store.vlcPort; val password = store.vlcPassword
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
