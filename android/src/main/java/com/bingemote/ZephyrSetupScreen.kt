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

/** Pairing with the Zephyr Player is its OWN screen (separate screen for
 *  Zephyr, separate screen for VLC), shaped like the standing VLC
 *  setup flow: instructions step, then find/address + code/pair, with
 *  a read-only check after a fresh pair. Re-entry opens the plain form; Back leaves. */
@Composable
fun ZephyrSetupScreen(
    remote: RemoteState,
    onDone: () -> Unit,
    onCancel: () -> Unit,
    initialStep: Int = 0,
    demo: String = "", // debug capture only (MainActivity, BuildConfig.DEBUG): preset UI state, no network, no store write
) {
    val store = remote.store
    val scope = rememberCoroutineScope()
    // Re-entering with a saved pairing opens on the pair step, which offers to continue with it.
    val entryStep = remember { if (initialStep > 0) initialStep else if (store.zephyrTarget() == null) 1 else 2 }
    var step by remember { mutableStateOf(entryStep) }
    // Same shape as the BingeMote setup: a PC is chosen from Find my PC or a typed address, and only then does the code card appear.
    var manualHost by remember { mutableStateOf("") }
    var chosen by remember { mutableStateOf<DiscoveredHelper?>(if (demo.isNotEmpty()) DiscoveredHelper("192.168.1.50", 47800, "MY-PC") else null) }
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
    fun checkAfterPair(zt: Store.ZephyrTarget) {
        val gen = ++checkGen
        checkState = PairCheck.Checking
        scope.launch(Dispatchers.IO) {
            val result = try {
                ZephyrClient.controlStatus(zt); if (zt.same(store.zephyrTarget())) PairCheck.Ok else PairCheck.CouldntCheck
            } catch (e: kotlinx.coroutines.CancellationException) { throw e
            } catch (e: ZephyrClient.BadToken) { PairCheck.BadToken
            } catch (e: Exception) { PairCheck.CouldntCheck }
            withContext(Dispatchers.Main) { if (gen == checkGen) checkState = result }
        }
    }

    fun pair() {
        val target = chosen ?: run { error = "Select your PC first."; return }
        val c = code.trim()
        if (c.length != 4 && c.length != 6) { error = "Enter the code Zephyr Player is showing on your PC - 4 digits, or 6 on an older Player."; return }
        if (busy) return
        busy = true
        error = null
        pairedName = null // a new attempt clears any earlier success card
        checkGen++; checkState = PairCheck.None // ...and invalidates any earlier check
        val h = target.host
        scope.launch(Dispatchers.IO) {
            try {
                val proof = ZephyrClient.pair(h, target.port, c)
                withContext(Dispatchers.Main) {
                    try {
                        // One locked, write-ahead save of identity, key, address and token from the same connection.
                        val outcome = store.saveZephyrPairing(proof, h, target.port, if (target.name == "PC") "" else target.name) // blank name clears a previous PC's name
                        if (outcome != SecurePairSave.Outcome.SAVED) {
                            pairedName = null
                            error = outcome.userMessage
                            return@withContext
                        }
                        code = ""
                        remote.refreshPairing()
                        pairedName = store.zephyrPcName.ifBlank { h }
                        store.zephyrTarget()?.let { checkAfterPair(it) }
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
                        else "Can't reach Zephyr Player at $h. Check it's open and on the same Wi-Fi."
                }
            } finally { busy = false }
        }
    }

    // Debug capture only: demo states that show the lower cards scroll to the end so the frame contains them.
    val demoScroll = rememberScrollState()
    LaunchedEffect(demo) { if (BuildConfig.DEBUG && demo.isNotEmpty() && demo != "chosen") { kotlinx.coroutines.delay(800); demoScroll.scrollTo(Int.MAX_VALUE) } }
    // Re-entry with a saved pairing opens on the form: Back leaves (like the VLC setup). Only the first-run wizard walks 2 -> 1.
    BackHandler { if (step > 1 && entryStep == 1) { step -= 1; error = null } else onCancel() }

    Column(
        Modifier.fillMaxSize().background(Bg).verticalScroll(demoScroll).padding(16.dp), // 16dp like BingeMote's setup
    ) {
        Spacer(Modifier.height(24.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("Set up Zephyr Player", color = TextC, fontSize = 24.sp, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
            androidx.compose.material3.TextButton(onClick = onCancel) { Text("Cancel", color = AccentText) }
        }
        Spacer(Modifier.height(18.dp))

        if (step == 1) {
            SetupCard {
                Text("1. Install and open the Zephyr Player on your PC.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                Spacer(Modifier.height(10.dp))
                Text("2. Open Zephyr Player (it sits in the tray by the clock), type a name under Pair new phone, and tap Pair. It shows a code for the next step.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
                Spacer(Modifier.height(10.dp))
                Text("3. Keep this phone and the PC on the same Wi-Fi.", color = TextC, fontSize = 14.sp, lineHeight = 20.sp)
            }
            Spacer(Modifier.height(18.dp))
            OrangeButton("Zephyr Player is open") { step = 2 }
        } else {
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
                    val isSel = chosen?.host == h.host
                    PressTile(
                        onTap = { chosen = DiscoveredHelper(h.host, h.port, h.name); manualHost = ""; code = ""; pairedName = null; error = null },
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
                    onValueChange = { manualHost = it; chosen = null; code = ""; pairedName = null; discoveryMessage = null; error = null },
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
            if (pairedName != null) {
                Spacer(Modifier.height(14.dp))
                SetupCard {
                    // A 401 on the first status check means the Player did not accept this token: no check mark, no "Paired" claim.
                    Text(if (checkState == PairCheck.BadToken) "Couldn't confirm pairing with $pairedName" else "\u2713 Paired with $pairedName", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                    when (checkState) {
                        PairCheck.Checking -> { Spacer(Modifier.height(6.dp)); Text("Checking with your PC\u2026", color = Muted, fontSize = 13.sp) }
                        PairCheck.BadToken -> { Spacer(Modifier.height(6.dp)); Text("Your PC didn't accept this pairing. Pair again with a new code.", color = ErrorText, fontSize = 13.sp) }
                        PairCheck.CouldntCheck -> {
                            Spacer(Modifier.height(6.dp))
                            Text("Couldn't check with your PC.", color = Muted, fontSize = 13.sp)
                            androidx.compose.material3.TextButton(onClick = {
                                val zt = store.zephyrTarget()
                                if (zt != null) checkAfterPair(zt)
                            }) { Text("Try again", color = AccentText) }
                        }
                        else -> {}
                    }
                    Spacer(Modifier.height(10.dp))
                    OrangeButton("Continue", onClick = onDone)
                }
            }
            // No bottom Back button: Cancel (top) and system Back still work (BingeMote 1.12.14).
        }
    }
}
