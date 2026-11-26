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

/** Pairing with the Zephyr Player is its OWN screen (separate screen for
 *  Zephyr, separate screen for VLC), shaped like the standing VLC
 *  setup flow: instructions step, then find/address + code/pair, with
 *  already-paired continue-or-start-over. Cancel/Back behave like that flow. */
@Composable
fun ZephyrSetupScreen(
    remote: RemoteState,
    onDone: () -> Unit,
    onCancel: () -> Unit,
    initialStep: Int = 0,
) {
    val store = remote.store
    val scope = rememberCoroutineScope()
    // Re-entering with a saved pairing opens on the pair step, which offers to continue with it.
    var step by remember { mutableStateOf(if (initialStep > 0) initialStep else if (store.zephyrToken.isBlank()) 1 else 2) }
    // Same shape as the BingeMote setup: a PC is chosen from Find my PC or a typed address, and only then does the code card appear.
    var manualHost by remember { mutableStateOf("") }
    var chosen by remember { mutableStateOf<DiscoveredHelper?>(null) }
    var code by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var scanning by remember { mutableStateOf(false) }
    var scannedOnce by remember { mutableStateOf(false) }
    var discoveryMessage by remember { mutableStateOf<String?>(null) }
    var found by remember { mutableStateOf<List<DiscoveredHelper>>(emptyList()) }
    var pairedName by remember { mutableStateOf<String?>(null) } // set after a pair on this screen: confirmation shows under the form

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

    fun pair() {
        val target = chosen ?: run { error = "Select your PC first."; return }
        val c = code.trim()
        if (c.length != 4 && c.length != 6) { error = "Enter the code Zephyr Player is showing on your PC - 4 digits, or 6 on an older Player."; return }
        if (busy) return
        busy = true
        error = null
        pairedName = null // a new attempt clears any earlier success card
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
                        code = ""
                        remote.refreshPairing()
                        pairedName = store.zephyrPcName.ifBlank { h }
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

    BackHandler { if (step > 1) { step -= 1; error = null } else onCancel() }

    Column(
        Modifier.fillMaxSize().background(Bg).verticalScroll(rememberScrollState()).padding(16.dp), // 16dp like BingeMote's setup
    ) {
        Spacer(Modifier.height(24.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("Set up Zephyr Player", color = TextC, fontSize = 24.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.weight(1f))
            androidx.compose.material3.TextButton(onClick = onCancel) { Text("Cancel", color = Orange) }
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
            if (store.zephyrToken.isNotBlank() && pairedName == null) {
                SetupCard {
                    Text("Already paired with ${store.zephyrPcName.ifBlank { store.zephyrHost }}", color = TextC, fontSize = 15.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(4.dp))
                    Text("This phone paired with your PC earlier. Continue with it, or start over to pair again with a new code.", color = Muted, fontSize = 13.sp)
                    Spacer(Modifier.height(10.dp))
                    OrangeButton("Continue with this PC", onClick = onDone)
                    Spacer(Modifier.height(6.dp))
                    androidx.compose.material3.TextButton(onClick = {
                        // Broken secure store: nothing is persisted, so "start over" is
                        // already true - the fail-closed write refusal is not an error here.
                        try { store.zephyrToken = "" } catch (e: SecureStoreUnavailable) { }
                        error = null
                    }) { Text("Start over with a new code", color = Muted) }
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
                    val isSel = chosen?.host == h.host
                    PressTile(
                        onTap = { chosen = DiscoveredHelper(h.host, store.zephyrPort, h.name); manualHost = ""; code = ""; pairedName = null; error = null },
                        background = if (isSel) OrangeGradient else CardGradient,
                        modifier = Modifier.fillMaxWidth().height(52.dp)
                            .then(if (isSel) Modifier else Modifier.border(1.dp, Orange, androidx.compose.foundation.shape.RoundedCornerShape(18.dp))),
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
                    Text("\u2713 Paired with $pairedName", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(10.dp))
                    OrangeButton("Continue", onClick = onDone)
                }
            }
            // No bottom Back button: Cancel (top) and system Back still work (BingeMote 1.12.14).
        }
    }
}
