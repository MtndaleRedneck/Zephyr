package com.bingemote

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.zephyr.remote.ZephyrClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private enum class PairCheck { None, Checking, Ok, BadToken, CouldntCheck }

/** Pairing with the Zephyr Player: the same screen and steps as the BingeMote setup (instructions step, then find or type the PC,
 *  code, Pair, and a result card with a read-only check). Only the app name, the Player's own error wording and the connection
 *  check differ. Back and Cancel follow the BingeMote rule: first run walks back one step, re-entry leaves. */
@Composable
fun ZephyrSetupScreen(
    remote: RemoteState,
    onDone: () -> Unit,
    onCancel: () -> Unit,
    initialStep: Int = 0,
    demoTop: Boolean = false, // debug capture only: do not scroll to the end
    demo: String = "", // debug capture only (MainActivity, BuildConfig.DEBUG): preset UI state, no network, no store write
) {
    val store = remote.store
    val scope = rememberCoroutineScope()
    // Re-entering with a saved pairing opens on the pair step, which offers to continue with it.
    val entryStep = remember { if (initialStep > 0) initialStep else if (store.zephyrToken.isBlank()) 1 else 2 }
    // An earlier pairing continued without a fresh code: the result card checks the saved pairing (BingeMote's Continue).
    var continueSaved by remember { mutableStateOf(false) }
    var step by remember { mutableStateOf(entryStep) }
    // Same shape as the BingeMote setup: a PC is chosen from Find my PC or a typed address, and only then does the code card appear.
    var manualHost by remember { mutableStateOf("") }
    var chosen by remember { mutableStateOf<DiscoveredHelper?>(if (demo.isNotEmpty() && demo != "found") DiscoveredHelper("192.168.1.50", 47800, "MY-PC") else null) }
    var code by remember { mutableStateOf(if (demo == "code") "1234" else "") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(if (demo == "error") "That code is wrong or expired. Check the code on your PC and try again." else null) }
    var scanning by remember { mutableStateOf(false) }
    var scannedOnce by remember { mutableStateOf(demo.isNotEmpty()) }
    var discoveryMessage by remember { mutableStateOf<String?>(null) }
    var found by remember { mutableStateOf<List<DiscoveredHelper>>(if (demo.isNotEmpty()) listOf(DiscoveredHelper("192.168.1.50", 47800, "MY-PC")) else emptyList()) }
    var checkState by remember { mutableStateOf(when (demo) { "success-ok" -> PairCheck.Ok; "success-bad" -> PairCheck.BadToken; "success-cant" -> PairCheck.CouldntCheck; "success-checking" -> PairCheck.Checking; else -> PairCheck.None }) } // post-pair status check; read-only, never touches the store
    var checkGen by remember { mutableStateOf(0) }
    var pairedName by remember { mutableStateOf<String?>(if (demo.startsWith("success")) "MY-PC" else null) } // set after a pair on this screen: confirmation shows under the form

    fun scan() {
        scanning = true
        discoveryMessage = null
        error = null
        scope.launch {
            found = withContext(Dispatchers.IO) { ZephyrClient.discoverPlayers() }
            scanning = false
            if (found.isEmpty()) discoveryMessage = "No PC answered. Check Zephyr Player is open on the PC, then try again, or type its address."
        }
    }
    // Auto-scan once when the pair step opens; the button re-scans on demand (BingeMote).
    LaunchedEffect(step) { if (step == 2 && !scannedOnce) { scannedOnce = true; scan() } }

    // One read-only GET /control/status right after a fresh pair. Calls ZephyrClient directly (never
    // RemoteState.pollZephyr, which writes unpaired flags). No store write and no refreshPairing on any outcome.
    // checkGen: each check (and each new pair attempt) bumps it, so a late result from an earlier
    // pair or an earlier Try again is dropped and cannot overwrite the current state.
    fun checkAfterPair(h: String, port: Int, token: String) {
        val gen = ++checkGen
        checkState = PairCheck.Checking
        scope.launch(Dispatchers.IO) {
            val result = try {
                ZephyrClient.controlStatus(h, port, token); PairCheck.Ok
            } catch (e: kotlinx.coroutines.CancellationException) { throw e
            } catch (e: ZephyrClient.BadToken) { PairCheck.BadToken
            } catch (e: Exception) { PairCheck.CouldntCheck }
            withContext(Dispatchers.Main) { if (gen == checkGen) checkState = result }
        }
    }

    fun pair() {
        val target = chosen ?: run { error = "Select your PC first."; return }
        val c = code.trim()
        if (c.length != 4 && c.length != 6) { error = "Enter the code Zephyr Player is showing on your PC - 4 digits, or 6 on an older Zephyr Player."; return }
        if (busy) return
        busy = true
        error = null
        pairedName = null // a new attempt clears any earlier success card
        continueSaved = false
        checkGen++; checkState = PairCheck.None // ...and invalidates any earlier check
        val h = target.host
        scope.launch(Dispatchers.IO) {
            try {
                val token = ZephyrClient.pair(h, target.port, c)
                withContext(Dispatchers.Main) {
                    try {
                        // Token FIRST: if secure storage is broken the fail-closed write throws before
                        // anything else is touched, so the app never claims a pairing that would vanish at restart.
                        store.zephyrToken = token
                        store.zephyrHost = h
                        store.zephyrPort = target.port
                        store.zephyrPcName = if (target.name == "PC") "" else target.name // blank clears a previous PC's name
                        remote.refreshPairing()
                        pairedName = store.zephyrPcName.ifBlank { h }
                        checkAfterPair(h, target.port, token)
                    } catch (e: SecureStoreUnavailable) {
                        error = "The pairing worked but couldn't be saved (secure storage problem on this phone). Nothing was saved here - on the PC, remove this phone from Zephyr Player's pairing list and pair again."
                    } catch (e: Exception) {
                        // Anything after the token write: the PC did pair, so never say it was unreachable.
                        pairedName = null
                        error = "The pairing worked but saving the details failed. Close and reopen the app; if this phone is not connected, remove it in Zephyr Player's pairing list and pair again."
                    }
                }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) {
                    pairedName = null
                    error = if (e is ZephyrClient.PairFailed) (e.message ?: "Pairing failed - try again.")
                        else "Couldn't reach Zephyr Player on $h. Check the PC is awake and try again."
                }
            } finally { busy = false }
        }
    }

    // A refused pairing: back to the code field for a fresh pair. The saved pairing stays until the new one lands.
    fun retryPair() {
        code = ""
        pairedName = null
        continueSaved = false
        checkGen++; checkState = PairCheck.None
        error = null
    }

    // Debug capture only: demo states that show the lower cards scroll to the end so the frame contains them.
    val demoScroll = rememberScrollState()
    LaunchedEffect(demo) { if (BuildConfig.DEBUG && demo.isNotEmpty() && demo != "chosen" && demo != "found" && !demoTop) { kotlinx.coroutines.delay(800); demoScroll.scrollTo(Int.MAX_VALUE) } }
    // BingeMote's rule: re-entry from Settings (setup finished) leaves on Back; first run walks pair -> install.
    val reentry = store.setupDone
    BackHandler { if (step > 1 && !reentry) { step -= 1; error = null } else onCancel() }

    Column(
        Modifier.fillMaxSize().background(Bg).verticalScroll(demoScroll).padding(16.dp), // 16dp like BingeMote's setup
    ) {
        Spacer(Modifier.height(24.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("Set up Zephyr Player", color = TextC, fontSize = 24.sp, fontWeight = FontWeight.Bold)
            if (store.setupDone) {
                Spacer(Modifier.weight(1f))
                androidx.compose.material3.TextButton(onClick = onCancel) { Text("Cancel", color = AccentText) }
            }
        }
        Spacer(Modifier.height(18.dp))

        if (step == 1) {
            SetupCard {
                Text("1. On your PC, download the Zephyr Player installer from the Zephyr release page and run it.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                Spacer(Modifier.height(10.dp))
                Text("2. Windows may warn about an unrecognized app. Click \"More info\", then \"Run anyway\". Zephyr Player is built by the same project as this app.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                Spacer(Modifier.height(10.dp))
                Text("3. The first time Zephyr Player opens it asks for Windows permission once - that is the only admin prompt, and it only opens Zephyr Player to your home network.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                Spacer(Modifier.height(10.dp))
                Text("4. Leave Zephyr Player running (it sits in the tray, by the clock). Open it, type a name under Pair new phone, and tap Pair - it shows a code for the next step.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
            }
            Spacer(Modifier.height(18.dp))
            OrangeButton("Zephyr Player is running") { step = 2 }
        } else {
            // Mid-setup recovery only (BingeMote): a pairing exists but setup never finished, so offer to continue with it.
            if (store.zephyrToken.isNotBlank() && !store.setupDone && pairedName == null && !continueSaved) {
                SetupCard {
                    Text("Already paired" + (if (store.zephyrPcName.isNotBlank()) " with ${store.zephyrPcName}" else ""), color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(8.dp))
                    Text("Continue with it, or enter a new code to pair again - only needed for a new phone, or if this phone was removed on the PC.", color = Muted, fontSize = 13.sp, lineHeight = 18.sp)
                    Spacer(Modifier.height(12.dp))
                    OrangeButton("Continue", onClick = {
                        error = null
                        continueSaved = true
                        checkAfterPair(store.zephyrHost, store.zephyrPort, store.zephyrToken)
                    })
                }
                Spacer(Modifier.height(14.dp))
            }
            SetupCard {
                Text("Find your PC", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                Spacer(Modifier.height(10.dp))
                OrangeButton(if (scanning) "Looking\u2026" else "Find my PC", enabled = !scanning, onClick = { scan() })
                if (scanning) {
                    Spacer(Modifier.height(10.dp))
                    androidx.compose.material3.CircularProgressIndicator(color = Orange, modifier = Modifier.size(28.dp))
                }
                found.forEach { h ->
                    Spacer(Modifier.height(8.dp))
                    val isSel = chosen == h
                    PressTile(
                        onTap = { chosen = h; manualHost = ""; code = ""; pairedName = null; continueSaved = false; error = null },
                        background = if (isSel) OrangeGradient else CardGradient,
                        modifier = Modifier.fillMaxWidth().height(52.dp)
                            .then(if (isSel) Modifier else Modifier.border(1.dp, Orange, androidx.compose.foundation.shape.RoundedCornerShape(18.dp))),
                    ) {
                        Text(
                            "${h.name}  (${h.host})",
                            color = if (isSel) OnAccent else TextC,
                            fontSize = 14.sp, fontWeight = FontWeight.Bold,
                        )
                    }
                }
                Spacer(Modifier.height(12.dp))
                Text("Or type the PC's address:", color = Muted, fontSize = 13.sp)
                Spacer(Modifier.height(6.dp))
                androidx.compose.material3.OutlinedTextField(
                    value = manualHost,
                    // N-5: a hand-typed address is a different PC - drop any stale choice.
                    onValueChange = { manualHost = it; chosen = null; code = ""; pairedName = null; continueSaved = false; discoveryMessage = null; error = null },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                    placeholder = { Text("192.168.1.50", color = Muted) },
                    keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.Uri),
                    colors = fieldColors(),
                )
                Spacer(Modifier.height(10.dp))
                OrangeButton("Use this address", enabled = manualHost.isNotBlank(), onClick = {
                    val address = manualHost.trim()
                    val host = address.substringBeforeLast(':', "")
                    val port = address.substringAfterLast(':', "").toIntOrNull()
                    if (address.contains(':') && (host.isBlank() || port == null || port !in 1..65535)) {
                        discoveryMessage = "Enter a PC address, with an optional port from 1 to 65535."
                    } else {
                        chosen = if (port != null) DiscoveredHelper(host, port, "PC") else DiscoveredHelper(address, store.zephyrPort, "PC")
                        discoveryMessage = null
                        code = ""
                        error = null
                    }
                })
                chosen?.takeIf { manualHost.isNotBlank() }?.let {
                    Spacer(Modifier.height(8.dp))
                    Text("Selected: ${it.host}:${it.port}", color = TextC, fontSize = 13.sp)
                }
                SetupMessage(discoveryMessage, true)
            }
            if (chosen != null) {
                Spacer(Modifier.height(14.dp))
                SetupCard {
                    Text("Pair", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(6.dp))
                    Text("Enter the code Zephyr Player is showing on your PC. No code on screen? Open Zephyr Player from the icon by the clock, type a name under Pair new phone, and tap Pair.", color = Muted, fontSize = 13.sp, lineHeight = 18.sp)
                    Spacer(Modifier.height(10.dp))
                    androidx.compose.material3.OutlinedTextField(
                        value = code,
                        onValueChange = {
                            code = it.filter(Char::isDigit).take(6)
                            // One less tap: a full 6th digit (older Player) pairs right away; a 4-digit code goes with the Pair button, since 4 is a prefix of 6.
                            if (code.length == 6 && !busy && chosen != null) pair()
                        },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true,
                        placeholder = { Text("Code from your PC", color = Muted) },
                        keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.NumberPassword),
                        colors = fieldColors(outline = Orange),
                    )
                    Spacer(Modifier.height(10.dp))
                    OrangeButton(if (busy) "Pairing\u2026" else "Pair", enabled = !busy, onClick = { pair() })
                    SetupMessage(error, true)
                }
            }
            // The result lives below the form (BingeMote): paired, then the check under it. Done only after a good check.
            if (pairedName != null || continueSaved) {
                Spacer(Modifier.height(14.dp))
                SetupCard {
                    // No check mark when the PC did not accept the token.
                    if (pairedName != null && checkState != PairCheck.BadToken) {
                        Text("\u2713 Paired with $pairedName", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                        Spacer(Modifier.height(10.dp))
                    }
                    when (checkState) {
                        PairCheck.Ok -> {
                            Text("Pairing confirmed", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                            Spacer(Modifier.height(10.dp))
                            Text("Your PC accepted this phone.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                            Spacer(Modifier.height(12.dp))
                            OrangeButton("Done", onClick = onDone)
                        }
                        PairCheck.CouldntCheck -> {
                            Text("Couldn't check with your PC", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                            Spacer(Modifier.height(10.dp))
                            Text("Zephyr Player on the PC didn't answer. Make sure it's running, then try again.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                            Spacer(Modifier.height(12.dp))
                            OrangeButton("Try again", onClick = {
                                val t = store.zephyrToken
                                if (t.isNotBlank()) checkAfterPair(store.zephyrHost, store.zephyrPort, t)
                            })
                        }
                        PairCheck.BadToken -> {
                            Text("Couldn't confirm pairing" + (if (pairedName != null) " with $pairedName" else ""), color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                            Spacer(Modifier.height(10.dp))
                            Text("Your PC didn't accept this pairing. Pair again with a new code from Zephyr Player on your PC.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                            Spacer(Modifier.height(12.dp))
                            OrangeButton("Pair again", onClick = { retryPair() })
                        }
                        else -> {
                            if (pairedName == null) { Text("Checking your pairing\u2026", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold); Spacer(Modifier.height(10.dp)) }
                            Text("Checking with your PC once\u2026", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                        }
                    }
                }
                Spacer(Modifier.height(6.dp))
            }
            // No bottom Back button: Cancel (top) and system Back still work (BingeMote 1.12.14).
        }
    }
}
