package com.bingemote

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.view.KeyEvent
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

class MainActivity : ComponentActivity() {

    override fun dispatchTouchEvent(ev: android.view.MotionEvent): Boolean {
        val a = ev.actionMasked
        // The one place every raw touch enters the app (the Compose root view
        // overrides dispatchTouchEvent, so a view-level OnTouchListener never
        // fires). A system cancel marks the gesture: it must not seek.
        if (a == android.view.MotionEvent.ACTION_CANCEL) RawTouch.cancelAt = ev.eventTime
        if (BuildConfig.DEBUG && (a == android.view.MotionEvent.ACTION_DOWN || a == android.view.MotionEvent.ACTION_UP || a == android.view.MotionEvent.ACTION_CANCEL)) {
            android.util.Log.i("BMSeek", "raw action=$a")
        }
        return super.dispatchTouchEvent(ev)
    }

    private lateinit var store: Store
    private lateinit var remote: RemoteState
    private var screen: MutableState<String>? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = Store(this)
        // 1.1 -> 1.2: retarget a saved BingeMote helper pairing to Zephyr VLC
        // Remote (port 8492) and flag the one-time "what changed" notice.
        store.migrateLegacyHelperPort()
        // Screenshot rig: pick the cancel-circle treatment for the cancel captures.
        if (BuildConfig.DEBUG) intent.getStringExtra("cancelstyle")?.let { store.cancelStyle = it }
        if (BuildConfig.DEBUG) intent.getStringExtra("pickeruri")?.let { DebugRig.pickerInitialUri = android.net.Uri.parse(it) }
        remote = RemoteState(this, store, lifecycleScope)
        if (BuildConfig.DEBUG && intent.getBooleanExtra("demostatus", false)) remote.debugSeedStatus()
        // Screenshot-rig only: render one status-notice review scenario per capture.
        // Polling is frozen (setupDone=false -> pollOnce no-ops) or leftover rig prefs
        // would re-trip unreachable/helperDown over the forced states mid-capture.
        if (BuildConfig.DEBUG && intent.hasExtra("statuscase")) store.setupDone = false
        if (BuildConfig.DEBUG) when (intent.getStringExtra("statuscase")) {
            "cur-g" -> remote.debugForceStatus(unreachable = true, helperDown = true)
            "prop-g" -> remote.debugForceStatus(unreachable = true, helperDown = false)
            "cur-e" -> { remote.debugSeedStatus(); remote.debugForceStatus(unreachable = false, helperDown = true) }
            "prop-e" -> { remote.debugSeedStatus(); remote.mockHeroNote = "Zephyr VLC Remote on the PC isn't answering - open it to use mouse, keyboard and PC volume." }
            "prop-unpaired" -> { remote.debugSeedStatus(); remote.mockHeroNote = "This phone isn't paired with the PC anymore - pair again." }
            // Post-1.11.0 production states: the REAL shipped notice branches
            // (post-debounce helper notice / 401 pair-again), no mock slot.
            "prod-e" -> { remote.debugSeedStatus(); remote.debugForceStatus(unreachable = false, helperDown = true, helperNotice = true) }
            "prod-unpaired" -> { remote.debugSeedStatus(); remote.debugForceStatus(unreachable = false, helperDown = true, unpaired = true) }
        }
        if (BuildConfig.DEBUG && intent.hasExtra("lockcontrols")) {
            store.lockScreenControls = true
            store.demoControls = true
            MediaControls.sync(this, store)
        }
        // CI screenshot state: force the update offer dialog with mock release notes.
        if (BuildConfig.DEBUG && intent.getStringExtra("screen") == "update") {
            ManualUpdate.offer = UpdateInfo(
                versionName = "9.9.9",
                notes = listOf("## v9.9.9", "Example improvement one", "Example improvement two"),
                apkUrl = "",
                size = 0,
                sha256 = null,
            )
        }
        setContent {
            val current = remember { mutableStateOf(
                if (BuildConfig.DEBUG && intent.hasExtra("screen")) intent.getStringExtra("screen") ?: "setup"
                else initialScreenForConfiguredInstall(store.setupDone)) }
            screen = current
            // Scroll positions live above the screen switch so leaving a screen and
            // coming back lands you where you were instead of back at the top.
            val settingsScroll = rememberScrollState()
            val setupScroll = rememberScrollState()
            val feedbackScroll = rememberScrollState()
            val moviesListState = rememberLazyListState()
            val pickerListState = rememberLazyListState()
            BingeMoteTheme {
                when (current.value) {
                    "setup" -> SetupScreen(
                        remote,
                        setupScroll,
                        // Teardown A1: a finished VLC pairing selects its own source, same as Zephyr.
                        onDone = { store.selectedSource = Source.VLC; remote.start(); current.value = "main" },
                        // Teardown A2: reachable only from Settings now - Cancel always exists.
                        onCancel = { current.value = "settings" },
                        demoVlcPending = BuildConfig.DEBUG && intent.hasExtra("vlcpending"),
                    )
                    "mouse" -> MouseScreen(remote) { current.value = "main" }
                    // CI screenshot states (debug builds only): open with the sheet forced open.
                    "keyboard" -> MouseScreen(remote, startKeyboardOpen = true) { current.value = "main" }
                    "subtitles" -> MainScreen(
                        remote,
                        onMouse = { current.value = "mouse" },
                        onMovies = { current.value = "movies" },
                        onSettings = { current.value = "settings" },
                        startSubtitlesOpen = true,
                    )
                    "movies" -> MoviesScreen(remote, moviesListState, onBack = { current.value = "main" }, demoResumeAsk = BuildConfig.DEBUG && intent.hasExtra("resumeask"))
                    // CI screenshot state (debug builds only): the resume dialog over the list.
                    "movies-resume" -> MoviesScreen(remote, moviesListState, onBack = { current.value = "main" }, demoResumeAsk = BuildConfig.DEBUG)
                    // CI screenshot state (debug builds only): Phone origin with demo rows.
                    "movies-phone" -> MoviesScreen(remote, moviesListState, onBack = { current.value = "main" }, demoPhone = BuildConfig.DEBUG)
                    "settings" -> SettingsScreen(
                        remote,
                        settingsScroll,
                        pickerListState,
                        onBack = { current.value = "main" },
                        onRepair = { current.value = "setup" },
                        onFeedback = { current.value = "feedback" },
                        onZephyrSetup = { current.value = "setup-zephyr" },
                    )
                    // Zephyr Player pairing is its OWN screen: separate
                    // screen for Zephyr, separate screen for VLC - never inline.
                    "setup-zephyr" -> ZephyrSetupScreen(
                        remote,
                        onDone = {
                            if (!store.setupDone) {
                                store.setupDone = true
                                store.selectedSource = Source.ZEPHYR
                                remote.start()
                                current.value = "main"
                            } else current.value = "settings"
                        },
                        onCancel = { current.value = "settings" },
                    )
                    // CI screenshot state (debug builds only): the Zephyr setup screen,
                    // captured under the historical "settings-pair" shot name.
                    "settings-pair" -> ZephyrSetupScreen(
                        remote,
                        onDone = { current.value = "settings" },
                        onCancel = { current.value = "settings" },
                    )
                    // CI screenshot states: step 2 of the Zephyr setup screen, fresh
                    // (find/address/code/pair) and with a saved pairing (continue/start-over).
                    "settings-pair-2" -> ZephyrSetupScreen(
                        remote,
                        onDone = { current.value = "settings" },
                        onCancel = { current.value = "settings" },
                        initialStep = 2,
                    )
                    "settings-pair-paired" -> {
                        // Debug capture only: fake a saved pairing so the already-paired
                        // card renders. Captured LAST in the rig; demo values never ship.
                        remote.store.zephyrHost = "192.168.1.50"
                        try { remote.store.zephyrToken = "demo-token" } catch (e: SecureStoreUnavailable) { }
                        remote.store.zephyrPcName = "Demo PC"
                        ZephyrSetupScreen(
                            remote,
                            onDone = { current.value = "settings" },
                            onCancel = { current.value = "settings" },
                            initialStep = 2,
                        )
                    }
                    "feedback" -> FeedbackScreen(remote, feedbackScroll) { current.value = "settings" }
                    else -> MainScreen(
                        remote,
                        onMouse = { current.value = "mouse" },
                        onMovies = { current.value = "movies" },
                        onSettings = { current.value = "settings" },
                    )
                }
                UpdateHost(store)
            }
        }
    }

    override fun onStart() {
        super.onStart()
        if (::remote.isInitialized && store.setupDone) remote.start()
        MediaControls.sync(this, store)
    }

    override fun onStop() {
        if (::remote.isInitialized) remote.stop()
        // A drag can be latched when the app backgrounds (call, screen timeout): release the
        // PC's left button so it doesn't stay held down while he's away.
        // Gate on the SELECTED source's pairing, not the helper token: a Zephyr-only
        // user's held button must release too (teardown C8).
        if (::remote.isInitialized && store.isPaired(store.selectedSource)) {
            val s = store
            HelperClient.io.execute { try { InputRouter.button(remote, "left", false) } catch (_: Exception) {} }
        }
        super.onStop()
    }

    /** Volume buttons control VLC volume (the movie list is touch only). */
    override fun onKeyDown(keyCode: Int, event: KeyEvent?): Boolean {
        if (!::remote.isInitialized || !store.setupDone) return super.onKeyDown(keyCode, event)
        return when (keyCode) {
            KeyEvent.KEYCODE_VOLUME_UP -> { remote.volumeStep(true); true }
            KeyEvent.KEYCODE_VOLUME_DOWN -> { remote.volumeStep(false); true }
            else -> super.onKeyDown(keyCode, event)
        }
    }
}

/** One-slot hand-off from the Settings update button to the offer dialog. */
object ManualUpdate {
    var offer by mutableStateOf<UpdateInfo?>(null)
}

private sealed interface UpdateUi {
    data class Offer(val info: UpdateInfo) : UpdateUi
    data class Downloading(val info: UpdateInfo, val progress: Float) : UpdateUi
    data class Failed(val message: String) : UpdateUi
}

/**
 * The always-on update check: asks GitHub once per open, offers the update,
 * and stays quiet when the network or GitHub is unavailable. Adapted from Listen Later.
 */
@Composable
fun UpdateHost(store: Store) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var updateUi by remember { mutableStateOf<UpdateUi?>(null) }
    var updateJob by remember { mutableStateOf<Job?>(null) }

    fun launchInstaller(f: File) {
        try {
            val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", f)
            context.startActivity(
                Intent(Intent.ACTION_VIEW)
                    .setDataAndType(uri, "application/vnd.android.package-archive")
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),
            )
        } catch (e: Exception) {
            updateUi = UpdateUi.Failed("Could not open the installer.")
        }
    }

    fun startUpdate(info: UpdateInfo) {
        updateUi = UpdateUi.Downloading(info, 0f)
        updateJob = scope.launch {
            try {
                val f = withContext(Dispatchers.IO) {
                    val existing = File(Updater.updateDir(context), "Zephyr-update.apk")
                    // Reuse the cache only when it IS this offer - a file downloaded for an
                    // older offer must never install (teardown 9a).
                    if (existing.exists() && Updater.cachedMatchesOffer(existing, info)) {
                        try { Updater.verify(context, existing); return@withContext existing }
                        catch (e: Exception) { existing.delete() }
                    } else if (existing.exists()) existing.delete()
                    val file = Updater.download(
                        context, info,
                        { pr -> updateUi = UpdateUi.Downloading(info, pr.coerceIn(0f, 1f)) },
                        { !isActive },
                    )
                    try { Updater.verify(context, file) }
                    catch (e: Updater.Failure) { file.delete(); throw e }
                    file
                }
                updateUi = null
                if (context.packageManager.canRequestPackageInstalls()) {
                    launchInstaller(f)
                } else {
                    updateUi = UpdateUi.Failed("Allow Zephyr to install updates, then check for updates again in Settings.")
                    context.startActivity(
                        Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}")),
                    )
                }
            } catch (e: Updater.Failure) {
                updateUi = if (e.msg == "cancelled") null else UpdateUi.Failed(e.msg)
            } catch (e: CancellationException) {
                Updater.clean(context)
                updateUi = null
            } catch (e: Exception) {
                Updater.clean(context)
                updateUi = UpdateUi.Failed("The update could not be installed.")
            }
        }
    }

    LaunchedEffect(Unit) {
        // Once per 6 hours, not per activity recreation (teardown 9c).
        if (System.currentTimeMillis() - store.lastUpdateCheckAt < 6L * 60 * 60 * 1000) return@LaunchedEffect
        // A leftover APK from an already-installed update is deleted on launch (teardown 9b):
        // verify() rejects anything not newer than the running version.
        withContext(Dispatchers.IO) {
            val cached = File(Updater.updateDir(context), "Zephyr-update.apk")
            if (cached.exists()) {
                try { Updater.verify(context, cached) } catch (e: Exception) { cached.delete() }
            }
        }
        val current = BuildConfig.VERSION_NAME
        val info = withContext(Dispatchers.IO) { Updater.check(current) } ?: return@LaunchedEffect
        // Stamp only after an answered check: an offline launch must not skip the
        // next 6 hours of auto-checks (S10).
        store.lastUpdateCheckAt = System.currentTimeMillis()
        if (info.versionName == store.skippedUpdate) return@LaunchedEffect
        updateUi = UpdateUi.Offer(info)
    }

    // The Settings "Check for updates" button drops its find here; a manual ask always
    // shows the offer, even for a version he skipped before.
    LaunchedEffect(ManualUpdate.offer) {
        ManualUpdate.offer?.let {
            updateUi = UpdateUi.Offer(it)
            ManualUpdate.offer = null
        }
    }

    updateUi?.let { u ->
        when (u) {
            is UpdateUi.Offer -> AlertDialog(
                onDismissRequest = { updateUi = null },
                containerColor = Card2,
                title = { Text("Zephyr ${u.info.versionName} is available", color = TextC) },
                text = {
                    Column {
                        Text("You have ${BuildConfig.VERSION_NAME}.", color = Muted)
                        if (u.info.notes.isNotEmpty()) {
                            Spacer(Modifier.height(10.dp))
                            val maxNotes = (LocalConfiguration.current.screenHeightDp * 0.38f).dp
                            Column(Modifier.heightIn(max = maxNotes).verticalScroll(rememberScrollState())) {
                                u.info.notes.forEach {
                                    if (it.startsWith("## ")) {
                                        Spacer(Modifier.height(4.dp))
                                        Text(it.drop(3), fontWeight = FontWeight.Bold, color = TextC)
                                    } else {
                                        Text("\u2022 $it", color = TextC)
                                    }
                                    Spacer(Modifier.height(4.dp))
                                }
                            }
                        }
                    }
                },
                confirmButton = { TextButton({ startUpdate(u.info) }) { Text("Update", color = Orange, fontWeight = FontWeight.Bold) } },
                // Skip sits with the other choices instead of floating, indented, inside the notes.
                dismissButton = {
                    Row {
                        TextButton({ store.skippedUpdate = u.info.versionName; updateUi = null }) { Text("Skip this version", color = Muted) }
                        TextButton({ updateUi = null }) { Text("Later", color = Muted) }
                    }
                },
            )
            is UpdateUi.Downloading -> AlertDialog(
                onDismissRequest = {},
                containerColor = Card2,
                title = { Text("Downloading ${u.info.versionName}", color = TextC) },
                text = {
                    Column {
                        LinearProgressIndicator(
                            progress = { u.progress },
                            modifier = Modifier.fillMaxWidth(),
                            color = Orange,
                            trackColor = Card,
                        )
                        Spacer(Modifier.height(8.dp))
                        Text("${(u.progress * 100).toInt()}%", color = Muted)
                    }
                },
                confirmButton = { TextButton({ updateJob?.cancel() }) { Text("Cancel", color = Muted) } },
            )
            is UpdateUi.Failed -> AlertDialog(
                onDismissRequest = { updateUi = null },
                containerColor = Card2,
                title = { Text("Update not installed", color = TextC) },
                text = { Text(u.message, color = Muted) },
                confirmButton = { TextButton({ updateUi = null }) { Text("OK", color = Orange) } },
            )
        }
    }
}
