package com.bingemote

import androidx.compose.foundation.clickable
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * First-run setup as a real wizard, one step on screen at a time:
 * 1) install the helper on the PC, 2) find the PC and pair with its 6-digit code,
 * 3) VLC password + movies folder. Back always works, and a saved pairing can be
 * continued or discarded - nobody gets stuck between steps.
 */
@Composable
fun SetupScreen(
    remote: RemoteState,
    scrollState: androidx.compose.foundation.ScrollState,
    onDone: () -> Unit,
    onCancel: () -> Unit,
    demoVlcPending: Boolean = false,
) {
    val store = remote.store
    val scope = rememberCoroutineScope()
    val uriHandler = androidx.compose.ui.platform.LocalUriHandler.current

    // Re-entering setup with a saved pairing opens on the pair step, which offers to continue with it.
    var step by remember {
        mutableStateOf(
            // CI screenshot state (debug builds only): straight to the finish step.
            if (demoVlcPending && BuildConfig.DEBUG) 3 else if (store.helperToken.isBlank()) 1 else 2,
        )
    }
    var scanning by remember { mutableStateOf(false) }
    var found by remember { mutableStateOf<List<DiscoveredHelper>>(emptyList()) }
    var chosen by remember { mutableStateOf<DiscoveredHelper?>(null) }
    var manualHost by remember { mutableStateOf("") }
    var code by remember { mutableStateOf("") }
    var pairing by remember { mutableStateOf(false) }
    var vlcPassword by remember { mutableStateOf("") }
    var moviesPath by remember { mutableStateOf("") }
    var testing by remember { mutableStateOf(false) }
    // VLC was open when the PC tried to write vlcrc: the PC finishes it when VLC closes.
    var vlcFinishPending by remember { mutableStateOf(false) }
    // Automatic-first VLC setup (helper 1.7+): the PC picked the password at pairing time.
    // "checking" while the phone verifies it, then "ok" or "refused".
    var autoSetup by remember { mutableStateOf(demoVlcPending && BuildConfig.DEBUG) }
    var autoState by remember { mutableStateOf(if (demoVlcPending && BuildConfig.DEBUG) "ok" else "checking") }
    // VLC is still running with its old settings: control starts when VLC reopens.
    var autoPending by remember { mutableStateOf(demoVlcPending && BuildConfig.DEBUG) }
    // VLC is simply closed (the PC attests the settings are written): control starts when it opens.
    var autoNotOpen by remember { mutableStateOf(false) }
    // Bump to re-run the automatic check after the PC was unreachable.
    var checkRun by remember { mutableStateOf(0) }
    // The manual password card stays as the backup, collapsed while the automatic path runs.
    var showManual by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    var messageIsError by remember { mutableStateOf(false) }

    fun say(text: String, isError: Boolean) { message = text; messageIsError = isError }

    // The real check, run once when step 3 opens after an automatic pair (and again per
    // Try again). The PC attests what is configured; a network error never becomes a
    // green Done on its own. The demo preview state starts at "ok" and skips this.
    LaunchedEffect(step, autoSetup, checkRun) {
        if (step != 3 || !autoSetup || autoState != "checking") return@LaunchedEffect
        when (withContext(Dispatchers.IO) { AutoSetupCheck.run(store) }) {
            AutoSetupCheck.Outcome.OK -> { autoPending = false; autoNotOpen = false; autoState = "ok" }
            AutoSetupCheck.Outcome.OK_DEFERRED -> { autoPending = true; autoNotOpen = false; autoState = "ok" }
            AutoSetupCheck.Outcome.OK_NOT_OPEN -> { autoPending = false; autoNotOpen = true; autoState = "ok" }
            AutoSetupCheck.Outcome.REFUSED -> autoState = "refused"
            AutoSetupCheck.Outcome.PC_UNREACHABLE -> autoState = "unreachable"
        }
    }

    // Back walks the steps, and on step 1 leaves setup entirely (teardown A2:
    // this screen is only reachable from Settings, so there is always a way out).
    BackHandler(enabled = true) {
        if (step > 1) { step -= 1; say("", false) } else onCancel()
    }

    fun scan() {
        scanning = true
        say("", false)
        scope.launch {
            found = withContext(Dispatchers.IO) { HelperClient.discover() }
            scanning = false
            if (found.isEmpty()) say("No PC found. Make sure Zephyr VLC Remote is running on the PC, or type its address below.", true)
        }
    }

    fun pair() {
        // A typed "host:port" keeps its own port instead of getting the default glued on.
        val manual = manualHost.trim()
        val target = chosen ?: run {
            val h = manual.substringBeforeLast(':', "")
            val p = manual.substringAfterLast(':', "").toIntOrNull()
            if (h.isNotBlank() && p != null) DiscoveredHelper(h, p, "PC")
            else DiscoveredHelper(manual, HelperClient.DEFAULT_PORT, "PC")
        }
        if (target.host.isBlank()) { say("Enter your PC's address or pick it above.", true); return }
        if (code.length != 6) { say("Enter the 6-digit code Zephyr VLC Remote is showing on your PC.", true); return }
        pairing = true
        say("", false)
        scope.launch {
            try {
                val paired = withContext(Dispatchers.IO) { HelperClient.pair(target.host, target.port, code) }
                // Token FIRST (fail-closed): a broken secure store throws before any
                // visible state changes, so no half-saved pairing.
                store.helperToken = paired.token
                store.host = target.host
                store.helperPort = target.port
                store.pcName = paired.name.ifBlank { target.name }
                remote.publishAddress(target.host, target.port, paired.token)
                remote.refreshPairing()
                // Automatic-first: the PC generated the VLC password and handed it over with
                // the pair. An old helper says nothing and step 3 stays the manual card.
                if (paired.vlcPassword != null) {
                    store.vlcPassword = paired.vlcPassword
                    autoSetup = true
                    autoState = "checking"
                    autoPending = paired.vlcPending
                }
                pairing = false
                say("", false)
                step = 3
            } catch (e: SecureStoreUnavailable) {
                pairing = false
                say("The pairing worked but couldn't be saved (secure storage problem on this phone). Nothing was saved here - on the PC, remove this phone from Zephyr VLC Remote's paired list, then pair again.", true)
            } catch (e: HelperClient.BadPairCode) {
                pairing = false
                say("That code didn't work. Get a fresh one: on your PC, open Zephyr VLC Remote from the icon by the clock, type a name under Pair new phone, and tap Pair.", true)
            } catch (e: Exception) {
                pairing = false
                say("Couldn't reach Zephyr VLC Remote on ${target.host}. Check the PC is awake and try again.", true)
            }
        }
    }

    fun startOver() {
        store.host = ""
        // Broken secure store: nothing persisted anyway; the refusal is not an error here.
        try { store.helperToken = "" } catch (e: SecureStoreUnavailable) { }
        remote.refreshPairing()
        store.pcName = ""
        remote.publishAddress("", HelperClient.DEFAULT_PORT, "")
        code = ""
        chosen = null
        manualHost = ""
        found = emptyList()
        say("Pairing cleared. On your PC, open Zephyr VLC Remote from the icon by the clock, type a name under Pair new phone, tap Pair, then enter the new code here.", false)
    }

    fun finish() {
        if (vlcPassword.isBlank()) { say("Enter the password you set in VLC's web interface.", true); return }
        if (store.helperToken.isBlank()) { say("Pair with your PC first.", true); return }
        try {
            store.vlcPassword = vlcPassword
        } catch (e: SecureStoreUnavailable) {
            say("Couldn't save the password (secure storage problem on this phone). Nothing was saved - close and reopen the app, then try again.", true)
            return
        }
        store.moviesPath = moviesPath.trim().ifBlank { store.moviesPath }
        testing = true
        say("", false)
        scope.launch {
            val vlcOk = try {
                withContext(Dispatchers.IO) { Vlc.status(store.host, store.vlcPort, store.vlcPassword) }
                true
            } catch (e: Exception) { false }
            val helperOk = try {
                withContext(Dispatchers.IO) { HelperClient.ping(store.host, store.helperPort, store.helperToken) }
            } catch (e: Exception) { false }
            testing = false
            when {
                vlcOk && helperOk -> {
                    // Hand the password to the PC: it writes vlcrc so a hand-opened VLC has
                    // the web remote too. VLC is open right now, so the PC defers the write
                    // and does it when VLC closes - say so, then let him leave with Done.
                    val deferred = withContext(Dispatchers.IO) {
                        try {
                            HelperClient.setupVlc(store.host, store.helperPort, store.helperToken, store.vlcPassword) == "vlc running"
                        } catch (e: Exception) { false }
                    }
                    store.setupDone = true
                    if (deferred) vlcFinishPending = true else onDone()
                }
                !vlcOk && helperOk -> {
                    // VLC may just be closed: the PC writes vlcrc so the next hand-open has the remote.
                    scope.launch(Dispatchers.IO) { try { HelperClient.setupVlc(store.host, store.helperPort, store.helperToken, store.vlcPassword) } catch (e: Exception) {} }
                    say("Zephyr VLC Remote answers, but VLC didn't. Check the password and that VLC is open with the web interface on.", true)
                }
                vlcOk && !helperOk -> say("VLC answers, but Zephyr VLC Remote didn't. Mouse mode won't work until Zephyr VLC Remote is running on the PC.", true)
                else -> say("Nothing answered. Check the PC is awake, VLC is open, and both are on the same Wi-Fi.", true)
            }
        }
    }

    Column(
        Modifier.fillMaxSize().background(Bg).verticalScroll(scrollState).padding(16.dp), // 16dp like every other screen
    ) {
        Spacer(Modifier.height(24.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("Set up VLC PC", color = TextC, fontSize = 24.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.weight(1f))
            TextButton(onClick = onCancel) { Text("Cancel", color = Orange) }
        }
        Spacer(Modifier.height(6.dp))
        Text(
            "Step $step of 3",
            color = Orange, fontSize = 14.sp, fontWeight = FontWeight.SemiBold,
        )
        Spacer(Modifier.height(18.dp))

        when (step) {
            1 -> Column {
                SetupCard {
                    Text("1. VLC control needs the free Zephyr VLC Remote PC app. Install it on your PC.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "Get Zephyr VLC Remote PC",
                        color = Orange, fontSize = 14.sp, fontWeight = FontWeight.SemiBold,
                        modifier = Modifier.clickable(role = androidx.compose.ui.semantics.Role.Button) {
                            // The helper's OWN release page: the general releases list
                            // also shows the old BingeMote recovery build, which is not
                            // the helper. Ship order guarantees this tag exists before
                            // the Android release that links to it.
                            uriHandler.openUri("https://github.com/MtndaleRedneck/Zephyr-Dev/releases/tag/vlc-remote-v1.0.0")
                        },
                    )
                    Spacer(Modifier.height(10.dp))
                    Text("2. If Windows warns about an unrecognized app, click \"More info\", then \"Run anyway\".", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                    Spacer(Modifier.height(10.dp))
                    Text("3. Allow the one Windows prompt during install. It only opens Zephyr VLC Remote to your home network.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                    Spacer(Modifier.height(10.dp))
                    Text("4. Leave Zephyr VLC Remote running (by the clock). It shows a 6-digit code.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                }
                Spacer(Modifier.height(18.dp))
                OrangeButton("Zephyr VLC Remote is running", onClick = { say("", false); step = 2 })
                SetupMessage(message, messageIsError)
            }

            2 -> Column {
                if (store.helperToken.isNotBlank()) {
                    SetupCard {
                        Text("Already paired" + (if (store.pcName.isNotBlank()) " with ${store.pcName}" else ""), color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                        Spacer(Modifier.height(8.dp))
                        Text("This phone paired with your PC earlier. Continue with it, or start over to pair again with a new code.", color = Muted, fontSize = 13.sp, lineHeight = 18.sp)
                        Spacer(Modifier.height(12.dp))
                        OrangeButton("Continue with this PC", onClick = { say("", false); step = 3 })
                        Spacer(Modifier.height(8.dp))
                        TextButton(onClick = { startOver() }) { Text("Start over with a new code", color = Muted) }
                    }
                    Spacer(Modifier.height(14.dp))
                }
                SetupCard {
                    Text("Find your PC", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(10.dp))
                    OrangeButton(if (scanning) "Looking\u2026" else "Find my PC", enabled = !scanning, onClick = { scan() })
                    if (scanning) {
                        Spacer(Modifier.height(10.dp))
                        CircularProgressIndicator(color = Orange, modifier = Modifier.size(28.dp))
                    }
                    found.forEach { h ->
                        Spacer(Modifier.height(8.dp))
                        val isSel = chosen == h
                        PressTile(
                            onTap = { chosen = h; manualHost = "" },
                            background = if (isSel) OrangeGradient else CardGradient,
                            modifier = Modifier.fillMaxWidth().height(52.dp),
                        ) {
                            Text(
                                "${h.name}  (${h.host})",
                                color = if (isSel) androidx.compose.ui.graphics.Color(0xFF1B1B1E) else TextC,
                                fontSize = 14.sp, fontWeight = FontWeight.Bold,
                            )
                        }
                    }
                    Spacer(Modifier.height(12.dp))
                    Text("Or type the PC's address:", color = Muted, fontSize = 13.sp)
                    Spacer(Modifier.height(6.dp))
                    OutlinedTextField(
                        value = manualHost,
                        onValueChange = { manualHost = it; if (it.isNotBlank()) chosen = null },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true,
                        placeholder = { Text("192.168.1.50", color = Muted) },
                        // Numbers-and-dots keyboard for an address.
                        keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.Uri),
                        colors = fieldColors(),
                    )
                }
                Spacer(Modifier.height(14.dp))
                SetupCard {
                    Text("Pair", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(6.dp))
                    Text("Enter the 6-digit code Zephyr VLC Remote is showing on your PC. It only works once. No code on screen? Open Zephyr VLC Remote from the icon by the clock, type a name under Pair new phone, and tap Pair.", color = Muted, fontSize = 13.sp, lineHeight = 18.sp)
                    Spacer(Modifier.height(10.dp))
                    OutlinedTextField(
                        value = code,
                        onValueChange = {
                            code = it.filter(Char::isDigit).take(6)
                            // One less tap: a full 6th digit pairs right away (needs a picked or typed PC).
                            if (code.length == 6 && !pairing && (chosen != null || manualHost.isNotBlank())) pair()
                        },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true,
                        placeholder = { Text("6-digit code", color = Muted) },
                        // Number pad for the code (it opened the full keyboard).
                        keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.NumberPassword),
                        visualTransformation = androidx.compose.ui.text.input.VisualTransformation.None,
                        colors = fieldColors(),
                    )
                    Spacer(Modifier.height(10.dp))
                    OrangeButton(if (pairing) "Pairing\u2026" else "Pair", enabled = !pairing, onClick = { pair() })
                    // Right under the button, not below Back where the keyboard hid it.
                    SetupMessage(message, messageIsError)
                }
                Spacer(Modifier.height(14.dp))
                TextButton(onClick = { say("", false); step = 1 }) { Text("Back", color = Muted) }
            }

            else -> Column {
                if (autoSetup) {
                    SetupCard {
                        when (autoState) {
                            "checking" -> {
                                Text("Setting up VLC remote control\u2026", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                                Spacer(Modifier.height(10.dp))
                                Text("Your PC picked the password. Checking it once\u2026", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                            }
                            "ok" -> {
                                Text("VLC remote control is set up", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                                Spacer(Modifier.height(10.dp))
                                Text(
                                    when {
                                        autoPending ->
                                            "VLC on the PC is still running with its old settings - phone control starts when VLC reopens."
                                        autoNotOpen ->
                                            "VLC isn't open on the PC right now - the password is saved, and phone control starts when you open it."
                                        else ->
                                            "Your PC picked the password and saved it - nothing to type into VLC."
                                    },
                                    color = TextC, fontSize = 14.sp, lineHeight = 20.sp,
                                )
                                Spacer(Modifier.height(12.dp))
                                OrangeButton("Done", onClick = { store.setupDone = true; onDone() })
                                SetupMessage(message, messageIsError)
                            }
                            "unreachable" -> {
                                Text("Couldn't check with your PC", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                                Spacer(Modifier.height(10.dp))
                                Text("Zephyr VLC Remote on the PC didn't answer. Make sure it's running, then try again.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                                Spacer(Modifier.height(12.dp))
                                OrangeButton("Try again", onClick = { autoState = "checking"; checkRun++ })
                                SetupMessage(message, messageIsError)
                            }
                            else -> {
                                Text("VLC refused the password", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                                Spacer(Modifier.height(10.dp))
                                Text("If you changed it on the PC, re-pair or set it by hand.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                                Spacer(Modifier.height(12.dp))
                                OrangeButton("Pair again", onClick = { startOver(); step = 2 })
                                SetupMessage(message, messageIsError)
                            }
                        }
                    }
                    if (autoState != "refused") {
                        Spacer(Modifier.height(8.dp))
                        TextButton(onClick = { showManual = !showManual }) {
                            Text(if (showManual) "Hide the manual setup" else "Set the password by hand instead", color = Muted)
                        }
                    }
                    Spacer(Modifier.height(6.dp))
                }
                if (!autoSetup || showManual || autoState == "refused") {
                SetupCard {
                    Text("Turn on VLC's web remote", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(10.dp))
                    Text("1. Open VLC on your PC.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                    Spacer(Modifier.height(6.dp))
                    Text("2. Tools - Preferences - at the bottom left, under \"Show settings\", pick All.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                    Spacer(Modifier.height(6.dp))
                    Text("3. Go to Interface - Main interfaces and tick Web.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                    Spacer(Modifier.height(6.dp))
                    Text("4. Under Main interfaces open Lua, and in Lua HTTP set a password. Type the same one below.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                    Spacer(Modifier.height(6.dp))
                    Text("5. Save and restart VLC. If Windows asks about the firewall, allow Private networks.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                }
                Spacer(Modifier.height(14.dp))
                SetupCard {
                    Text("VLC password", color = TextC, fontSize = 15.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(8.dp))
                    OutlinedTextField(
                        value = vlcPassword,
                        onValueChange = { vlcPassword = it },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true,
                        placeholder = { Text("The password from step 4", color = Muted) },
                        colors = fieldColors(),
                        visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(),
                    )
                    Spacer(Modifier.height(14.dp))
                    Text("Movies folder (optional)", color = TextC, fontSize = 15.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(8.dp))
                    OutlinedTextField(
                        value = moviesPath,
                        onValueChange = { moviesPath = it },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true,
                        placeholder = { Text("C:\\Users\\you\\Videos", color = Muted) },
                        colors = fieldColors(),
                    )
                    Spacer(Modifier.height(12.dp))
                    if (vlcFinishPending) {
                        OrangeButton("Done", onClick = { onDone() })
                        Spacer(Modifier.height(8.dp))
                        Text(
                            "VLC is open on the PC - Zephyr VLC Remote will finish setting it up when VLC closes.",
                            color = Muted, fontSize = 13.sp,
                        )
                    } else {
                        OrangeButton(if (testing) "Checking\u2026" else "Finish", enabled = !testing, onClick = { finish() })
                    }
                    SetupMessage(message, messageIsError)
                }
                }
                Spacer(Modifier.height(14.dp))
                TextButton(onClick = { say("", false); step = 2 }) { Text("Back", color = Muted) }
            }
        }

        Spacer(Modifier.height(24.dp))
    }
}

@Composable
fun SetupCard(content: @Composable ColumnScope.() -> Unit) {
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(20.dp)).background(CardGradient).padding(16.dp),
        content = content,
    )
}

@Composable
private fun SetupMessage(message: String?, isError: Boolean) {
    if (message.isNullOrBlank()) return
    Spacer(Modifier.height(10.dp))
    Text(message, color = if (isError) ErrorRed else Orange, fontSize = 13.sp, lineHeight = 18.sp)
}

@Composable
fun OrangeButton(label: String, enabled: Boolean = true, onClick: () -> Unit) {
    // One primary button size app-wide: 48dp tall, 14dp corners (was 52/16 here, 46/14 in Settings).
    PressTile(
        onTap = { if (enabled) onClick() },
        background = if (enabled) OrangeGradient else CardGradient,
        modifier = Modifier.fillMaxWidth().height(48.dp),
        corner = 14.dp,
        enabled = enabled,
    ) {
        Text(label, color = if (enabled) androidx.compose.ui.graphics.Color(0xFF1B1B1E) else Dimmed, fontSize = 15.sp, fontWeight = FontWeight.Bold)
    }
}
