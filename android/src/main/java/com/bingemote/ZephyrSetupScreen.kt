package com.bingemote

import androidx.activity.compose.BackHandler
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
    var host by remember { mutableStateOf(store.zephyrHost) }
    var code by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var finding by remember { mutableStateOf(false) }
    var findNote by remember { mutableStateOf<String?>(null) }
    var found by remember { mutableStateOf<List<DiscoveredHelper>>(emptyList()) }
    var foundName by remember { mutableStateOf("") }

    BackHandler { if (step > 1) { step -= 1; error = null } else onCancel() }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 18.dp, vertical = 10.dp),
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("Set up Zephyr Player", color = TextC, fontSize = 20.sp, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
            androidx.compose.material3.TextButton(onClick = onCancel) { Text("Cancel", color = Orange) }
        }
        Text("Step $step of 2", color = Orange, fontSize = 13.sp)
        Spacer(Modifier.height(12.dp))

        if (step == 1) {
            SetupCard {
                Text("1. Install and open the Zephyr Player on your PC.", color = TextC, fontSize = 14.sp)
                Spacer(Modifier.height(6.dp))
                Text("2. Find the 6-digit code on its home screen, or in its Settings > Android remote.", color = TextC, fontSize = 14.sp)
                Spacer(Modifier.height(6.dp))
                Text("3. Keep this phone and the PC on the same Wi-Fi.", color = TextC, fontSize = 14.sp)
            }
            Spacer(Modifier.height(14.dp))
            OrangeButton("Zephyr Player is open") { step = 2 }
        } else {
            if (store.zephyrToken.isNotBlank()) {
                SetupCard {
                    Text("Already paired with ${store.zephyrPcName.ifBlank { store.zephyrHost }}", color = TextC, fontSize = 15.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(4.dp))
                    Text("This phone paired with your PC earlier. Continue with it, or start over to pair again with a new code.", color = Muted, fontSize = 13.sp)
                    Spacer(Modifier.height(10.dp))
                    OrangeButton("Continue with this PC", onClick = onDone)
                    Spacer(Modifier.height(6.dp))
                    androidx.compose.material3.TextButton(onClick = {
                        store.zephyrToken = ""
                        error = null
                    }) { Text("Start over with a new code", color = Muted) }
                }
                Spacer(Modifier.height(14.dp))
            }
            ZephyrPairCard(
                host = host, onHost = { host = it },
                code = code, onCode = { code = it },
                busy = busy, error = error,
                finding = finding, findNote = findNote, found = found,
                onFind = {
                    if (finding) return@ZephyrPairCard
                    finding = true
                    findNote = null
                    found = emptyList()
                    scope.launch(Dispatchers.IO) {
                        val pcs = ZephyrClient.discoverPlayers()
                        withContext(Dispatchers.Main) {
                            found = pcs
                            when {
                                pcs.isEmpty() -> findNote = "No PC found. Make sure Zephyr Player is open, or type the PC's address below."
                                pcs.size == 1 -> {
                                    host = pcs[0].host
                                    foundName = pcs[0].name
                                    findNote = "Found ${pcs[0].name} (${pcs[0].host})."
                                }
                                else -> findNote = "Several PCs answered - tap one, or type the address below."
                            }
                        }
                        finding = false
                    }
                },
                onPick = { pc ->
                    host = pc.host
                    foundName = pc.name
                    findNote = "Found ${pc.name} (${pc.host})."
                    found = emptyList()
                },
                onPair = {
                    if (busy) return@ZephyrPairCard
                    busy = true
                    error = null
                    val h = host.trim()
                    val c = code.trim()
                    scope.launch(Dispatchers.IO) {
                        try {
                            val token = ZephyrClient.pair(h, store.zephyrPort, c)
                            withContext(Dispatchers.Main) {
                                store.zephyrHost = h
                                store.zephyrToken = token
                                if (foundName.isNotBlank()) store.zephyrPcName = foundName
                                code = ""
                                onDone()
                            }
                        } catch (e: Exception) {
                            withContext(Dispatchers.Main) {
                                // Keep ZephyrClient's plain code errors; raw network failures
                                // become one plain sentence (teardown A3).
                                val m = e.message ?: ""
                                error = if (m.startsWith("Wrong code") || m.startsWith("Too many") || m.startsWith("This PC already")) m
                                    else "Can't reach Zephyr Player at $h. Check it's open and on the same Wi-Fi."
                            }
                        } finally { busy = false }
                    }
                },
            )
            Spacer(Modifier.height(10.dp))
            androidx.compose.material3.TextButton(onClick = { step = 1; error = null }) { Text("Back", color = Muted) }
        }
    }
}
