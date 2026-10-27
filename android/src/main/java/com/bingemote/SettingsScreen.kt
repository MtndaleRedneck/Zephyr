package com.bingemote

import android.content.Intent
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.waitForUpOrCancellation
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.ScrollState
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardType
import com.zephyr.remote.ZephyrClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material3.Icon
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.platform.LocalContext
import kotlinx.coroutines.launch

@Composable
fun SettingsScreen(
    remote: RemoteState,
    scrollState: ScrollState,
    pickerListState: LazyListState,
    onBack: () -> Unit,
    onRepair: () -> Unit,
    onFeedback: () -> Unit,
    // CI screenshot state (debug only): open with the Zephyr pairing form expanded.
    onZephyrSetup: () -> Unit = {},
) {
    val store = remote.store
    val context = LocalContext.current
    var mainSkip by remember { mutableStateOf(store.skipMainSec) }
    var secondarySkip by remember { mutableStateOf(store.skipSecondarySec) }
    var haptics by remember { mutableStateOf(store.haptics) }
    var lockControls by remember { mutableStateOf(store.lockScreenControls) }
    var flash by remember { mutableStateOf(store.buttonFlash) }
    var cancelStyle by remember { mutableStateOf(store.cancelStyle) }
    var pickingMovies by remember { mutableStateOf(false) }
    if (pickingMovies) {
        FolderPickerScreen(
            remote,
            pickerListState,
            onPick = {
            // The Zephyr picker already saved on the player; moviesPath is VLC's setting.
            if (store.selectedSource != Source.ZEPHYR) store.moviesPath = it
            pickingMovies = false
        },
            onCancel = { pickingMovies = false },
        )
        return
    }
    Column(
        Modifier.fillMaxSize().background(Bg).verticalScroll(scrollState).padding(16.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            HeaderIconButton(onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back", tint = TextC) }
            Spacer(Modifier.width(12.dp))
            Text("Settings", color = TextC, fontSize = 20.sp, fontWeight = FontWeight.Bold)
        }
        if (store.secureStoreBroken) {
            Spacer(Modifier.height(10.dp))
            Box(Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Card2).padding(horizontal = 16.dp, vertical = 12.dp)) {
                Text(
                    "Your saved sign-ins couldn't be opened this time (secure storage problem). Nothing was deleted. Close and reopen the app; new pairings can't be saved until this clears.",
                    color = ErrorRed, fontSize = 13.sp, lineHeight = 18.sp,
                )
            }
        }
        Spacer(Modifier.height(18.dp))
        BackHandler { onBack() }

        // "Connected Sources" card: the orange source labels ARE the
        // pairing entry points (locked design; the standalone repair button is removed).
        // Each label opens that source's OWN setup screen -
        // never an inline expansion.
        SettingsSection("Connected Sources") {
            // A cancelled setup can leave a helper token with no finished pairing: only a
            // COMPLETE setup shows the PC name (teardown A2 edge).
            SourceRow("VLC PC", if (store.isConfigured) store.pcName.ifBlank { store.host } else "Not paired", onLabelClick = onRepair)
            Spacer(Modifier.height(6.dp))
            SourceRow(
                "Zephyr PC",
                // C17: a paired Zephyr source names the PC and its address:port,
                // like the VLC row - never a bare "Paired".
                if (!remote.zephyrPaired) "Not paired"
                else (remote.store.zephyrPcName.ifBlank { "PC" }) +
                    " (" + remote.store.zephyrHost + ":" + remote.store.zephyrPort + ")",
                onLabelClick = onZephyrSetup,
            )
        }
        Spacer(Modifier.height(14.dp))

        SettingsSection("Assigned Movies folder") {
            // Phone folder picker (SAF): the Movies screen's Phone origin reads this.
            val pickPhoneTree = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
                if (uri != null) {
                    runCatching {
                        context.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
                    }
                    store.moviesPhoneTreeUri = uri.toString()
                }
            }
            // Read-only: Browse PC folders is the only way to change it, so a typo can't break the Movies tab.
            // With Zephyr selected the row is the PLAYER's library folder (teardown C7), read live.
            val zephyrSelected = store.selectedSource == Source.ZEPHYR
            var zephyrFolderRow by remember { mutableStateOf<String?>(null) }
            var zephyrFolderFailed by remember { mutableStateOf(false) }
            LaunchedEffect(zephyrSelected) {
                if (zephyrSelected && store.zephyrToken.isNotBlank()) {
                    val row = withContext(kotlinx.coroutines.Dispatchers.IO) {
                        try {
                            com.zephyr.remote.ZephyrClient.libraryFolder(
                                store.zephyrHost, store.zephyrPort, store.zephyrToken)
                        } catch (e: Exception) { null }
                    }
                    // NEW-2: loading, unpaired and failed are different rows - a
                    // null answer is the only failure, not the first frames.
                    zephyrFolderFailed = row == null
                    zephyrFolderRow = row
                }
            }
            val folderRowText = if (zephyrSelected) when {
                store.zephyrToken.isBlank() -> "Pair with the PC first."
                zephyrFolderFailed -> "Couldn't read it from the PC right now."
                zephyrFolderRow == null -> "Reading from the PC..."
                else -> zephyrFolderRow!!.ifBlank { "Not set yet. Tap Browse PC folders." }
            }
            else store.moviesPath.ifBlank { "Not set yet. Tap Browse PC folders." }
            Box(
                Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Card2).padding(horizontal = 16.dp, vertical = 15.dp),
            ) {
                Text(
                    folderRowText,
                    color = if (folderRowText.startsWith("Not set")) Muted else TextC,
                    fontSize = 14.sp,
                )
            }
            Spacer(Modifier.height(10.dp))
            PressTile(
                onTap = { pickingMovies = true },
                background = OrangeGradient,
                modifier = Modifier.fillMaxWidth().height(48.dp),
                corner = 14.dp,
            ) {
                Text(
                    "Browse PC folders",
                    color = androidx.compose.ui.graphics.Color(0xFF1B1B1E),
                    fontSize = 14.sp, fontWeight = FontWeight.Bold,
                )
            }
            Spacer(Modifier.height(10.dp))
            Box(
                Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Card2).padding(horizontal = 16.dp, vertical = 15.dp),
            ) {
                Text(
                    phoneFolderDisplay(store.moviesPhoneTreeUri),
                    color = if (store.moviesPhoneTreeUri.isBlank()) Muted else TextC,
                    fontSize = 14.sp,
                )
            }
            Spacer(Modifier.height(10.dp))
            PressTile(
                onTap = { pickPhoneTree.launch(null) },
                background = OrangeGradient,
                modifier = Modifier.fillMaxWidth().height(48.dp),
                corner = 14.dp,
            ) {
                Text(
                    "Browse phone folders",
                    color = androidx.compose.ui.graphics.Color(0xFF1B1B1E),
                    fontSize = 14.sp, fontWeight = FontWeight.Bold,
                )
            }
        }
        Spacer(Modifier.height(14.dp))

        SettingsSection("Skip buttons") {
            Text("Primary skip buttons", color = TextC, fontSize = 14.sp)
            Spacer(Modifier.height(8.dp))
            ChoiceRow(
                options = listOf(5 to "5s", 10 to "10s", 15 to "15s", 30 to "30s"),
                selected = mainSkip,
            ) { mainSkip = it; store.skipMainSec = it }
            Spacer(Modifier.height(14.dp))
            Text("Secondary skip buttons", color = TextC, fontSize = 14.sp)
            Spacer(Modifier.height(8.dp))
            ChoiceRow(
                options = listOf(15 to "15s", 30 to "30s", 60 to "1m", 120 to "2m", 180 to "3m"),
                selected = secondarySkip,
            ) { secondarySkip = it; store.skipSecondarySec = it }
        }
        Spacer(Modifier.height(14.dp))

        SettingsSection("Feel") {
            ToggleRow("Vibrate on press", haptics) { haptics = it; store.haptics = it }
            Spacer(Modifier.height(14.dp))
            Text("Button flash", color = TextC, fontSize = 14.sp)
            Spacer(Modifier.height(10.dp))
            ChoiceRow(
                options = listOf(0 to "Skip rings only", 1 to "All buttons"),
                selected = flash,
            ) { flash = it; store.buttonFlash = it }
            Spacer(Modifier.height(14.dp))
            Text("Cancel button style", color = TextC, fontSize = 14.sp)
            Spacer(Modifier.height(10.dp))
            ChoiceRow(
                options = listOf(0 to "Fills in", 1 to "Ring only"),
                selected = if (cancelStyle == "ring") 1 else 0,
            ) { cancelStyle = if (it == 1) "ring" else "fills"; store.cancelStyle = cancelStyle }
        }
        Spacer(Modifier.height(14.dp))

        SettingsSection("Lock screen") {
            ToggleRow("Controls on lock screen", lockControls) {
                lockControls = it; store.lockScreenControls = it
                MediaControls.sync(context, store)
            }
            Spacer(Modifier.height(8.dp))
            Text("Play, pause, and skip from your lock screen while a movie plays.", color = Muted, fontSize = 13.sp)
        }
        Spacer(Modifier.height(14.dp))

        SettingsSection("Phone can't connect?") {
            Text(
                // Per source (S3): the tip names the app you actually use.
                if (store.selectedSource == Source.ZEPHYR)
                    "Open Zephyr Player on the PC and check both are on the same Wi-Fi."
                else
                    "Open Zephyr VLC Remote on the PC and tap \u201CThis is my home Wi-Fi\u201D.",
                color = TextC, fontSize = 14.sp,
            )
        }
        Spacer(Modifier.height(14.dp))

        SettingsSection("Feedback & bugs") {
            val view = androidx.compose.ui.platform.LocalView.current
            Row(
                Modifier
                    .fillMaxWidth()
                    .heightIn(min = 48.dp)
                    .clip(RoundedCornerShape(14.dp))
                    .clickable(role = androidx.compose.ui.semantics.Role.Button) {
                        tapHaptic(view, haptics)
                        onFeedback()
                    },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                // No hard line break: the text wraps on its own at any font size.
                Text(
                    "Send feedback, report a bug, or suggest a feature",
                    color = TextC, fontSize = 14.sp, fontWeight = FontWeight.SemiBold,
                    lineHeight = 20.sp, modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(8.dp))
                Icon(
                    Icons.AutoMirrored.Filled.KeyboardArrowRight,
                    contentDescription = null, tint = Muted, modifier = Modifier.size(24.dp),
                )
            }
            Spacer(Modifier.height(6.dp))
            Text("You see exactly what is sent before you send it.", color = Muted, fontSize = 12.sp)
        }
        Spacer(Modifier.height(14.dp))

        SettingsSection("Updates") {
            var checking by remember { mutableStateOf(false) }
            var result by remember { mutableStateOf("") }
            val scope = rememberCoroutineScope()
            PressTile(
                onTap = {
                    if (checking) return@PressTile
                    checking = true
                    result = ""
                    scope.launch {
                        val outcome = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                            Updater.checkDetailed(BuildConfig.VERSION_NAME)
                        }
                        checking = false
                        when (outcome) {
                            is Updater.CheckOutcome.Newer -> {
                                result = "Version " + outcome.info.versionName + " is available."
                                ManualUpdate.offer = outcome.info
                            }
                            Updater.CheckOutcome.NoRelease -> result = "No update is available right now."
                            Updater.CheckOutcome.UpToDate -> result = "You're up to date."
                            Updater.CheckOutcome.Unavailable -> result = "Couldn't check right now. Try again later."
                        }
                    }
                },
                background = OrangeGradient,
                modifier = Modifier.fillMaxWidth().height(48.dp),
                corner = 14.dp,
                enabled = !checking,
            ) {
                Text(
                    if (checking) "Checking\u2026" else "Check for updates",
                    color = androidx.compose.ui.graphics.Color(0xFF1B1B1E),
                    fontSize = 14.sp, fontWeight = FontWeight.Bold,
                )
            }
            if (result.isNotEmpty()) {
                Spacer(Modifier.height(8.dp))
                Text(result, color = Muted, fontSize = 12.sp)
            }
            Spacer(Modifier.height(8.dp))
            Text("Version ${BuildConfig.VERSION_NAME}", color = Muted, fontSize = 12.sp)
        }
        // Bottom gap = the screen's own 16.dp padding, same as the sides (the old
        // extra 28.dp spacer left a visibly bigger gap under the last card).
    }
}

@Composable
fun fieldColors() = OutlinedTextFieldDefaults.colors(
    focusedTextColor = TextC, unfocusedTextColor = TextC,
    focusedBorderColor = Orange, unfocusedBorderColor = TrackGray,
    cursorColor = Orange,
)

@Composable
fun SettingsSection(title: String, content: @Composable ColumnScope.() -> Unit) {
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(20.dp)).background(CardGradient).padding(16.dp),
    ) {
        if (title.isNotEmpty()) {
            Text(title, color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.height(10.dp))
        }
        content()
    }
}

@Composable
fun SettingsRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 3.dp)) {
        Text(label, color = Muted, fontSize = 14.sp, modifier = Modifier.width(80.dp))
        Text(value, color = TextC, fontSize = 14.sp)
    }
}

@Composable
fun ToggleRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    // The whole row toggles, not just the switch.
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp)
            .toggleable(value = checked, role = androidx.compose.ui.semantics.Role.Switch, onValueChange = onChange)
            .padding(vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, color = TextC, fontSize = 14.sp, modifier = Modifier.weight(1f))
        Switch(
            checked = checked, onCheckedChange = null,
            colors = SwitchDefaults.colors(checkedTrackColor = Orange, uncheckedTrackColor = TrackGray),
        )
    }
}

@Composable
fun ChoiceRow(options: List<Pair<Int, String>>, selected: Int, onPick: (Int) -> Unit) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        options.forEach { (value, label) ->
            val isSel = value == selected
            PressTile(
                onTap = { onPick(value) },
                background = if (isSel) OrangeGradient else CardGradient,
                modifier = Modifier.weight(1f).height(44.dp),
                corner = 14.dp,
            ) {
                Text(
                    label,
                    color = if (isSel) androidx.compose.ui.graphics.Color(0xFF1B1B1E) else Muted,
                    fontSize = 14.sp, fontWeight = FontWeight.Bold,
                )
            }
        }
    }
}


/** Source label (orange, tappable pairing entry point) + state value row for the
 * approved Connected Sources card. */
@Composable
fun SourceRow(label: String, value: String, onLabelClick: () -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        PressTile(onTap = onLabelClick, background = OrangeGradient,
            modifier = Modifier.width(112.dp).height(48.dp), corner = 14.dp) {
            Text(label, color = androidx.compose.ui.graphics.Color(0xFF1B1B1E), fontSize = 14.sp, fontWeight = FontWeight.Bold)
        }
        Spacer(Modifier.width(12.dp))
        Text(value, color = if (value == "Not paired" || value == "Not set up") Muted else TextC,
            fontSize = 14.sp, textAlign = androidx.compose.ui.text.style.TextAlign.End,
            modifier = Modifier.weight(1f))
    }
}

/** Zephyr Player pairing form, opened from the Zephyr PC source label. Trades the
 * on-screen 6-digit code for the pairing token (protocol.md Pairing). */
@Composable
fun ZephyrPairCard(
    host: String, onHost: (String) -> Unit,
    code: String, onCode: (String) -> Unit,
    busy: Boolean, error: String?, onPair: () -> Unit,
    finding: Boolean, findNote: String?, found: List<DiscoveredHelper>,
    onFind: () -> Unit, onPick: (DiscoveredHelper) -> Unit,
) {
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(CardGradient).padding(16.dp),
    ) {
        Text("Pair with the Zephyr Player", color = TextC, fontSize = 16.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(4.dp))
        Text("Open the player on the PC and enter the code it shows.", color = Muted, fontSize = 13.sp)
        Spacer(Modifier.height(10.dp))
        // Same automated discovery as the helper setup ("Find my PC").
        OrangeButton(if (finding) "Looking\u2026" else "Find my PC", enabled = !finding, onClick = onFind)
        if (findNote != null) {
            Spacer(Modifier.height(6.dp))
            Text(findNote, color = Muted, fontSize = 13.sp)
        }
        if (found.size > 1) {
            Spacer(Modifier.height(6.dp))
            for (pc in found) {
                Text("${pc.name} (${pc.host})", color = Orange, fontSize = 14.sp,
                    modifier = Modifier.padding(vertical = 4.dp).clickable { onPick(pc) })
            }
        }
        Spacer(Modifier.height(10.dp))
        OutlinedTextField(
            value = host, onValueChange = onHost, singleLine = true,
            label = { Text("PC address") }, colors = fieldColors(),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(8.dp))
        OutlinedTextField(
            value = code, onValueChange = onCode, singleLine = true,
            label = { Text("6-digit code") }, colors = fieldColors(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
            modifier = Modifier.fillMaxWidth(),
        )
        if (error != null) {
            Spacer(Modifier.height(8.dp))
            Text(error, color = ErrorRed, fontSize = 13.sp)
        }
        Spacer(Modifier.height(10.dp))
        OrangeButton(if (busy) "Pairing\u2026" else "Pair", enabled = !busy && code.trim().length == 6 && host.isNotBlank(), onClick = onPair)
    }
}

/** SAF tree URI -> the friendly path the user recognizes (shows
 * /storage/emulated/0/Movies). Unknown roots show their decoded document id. */
fun phoneFolderDisplay(treeUri: String): String {
    if (treeUri.isBlank()) return "Not set yet. Tap Browse phone folders."
    val decoded = java.net.URLDecoder.decode(treeUri.substringAfterLast("/tree/"), "UTF-8")
    return when {
        decoded.startsWith("primary:") -> "/storage/emulated/0/" + decoded.removePrefix("primary:")
        decoded.isBlank() -> treeUri
        else -> decoded
    }
}
