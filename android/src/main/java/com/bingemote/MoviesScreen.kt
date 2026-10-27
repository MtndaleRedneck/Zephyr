package com.bingemote

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.ui.platform.LocalContext
import com.zephyr.remote.PhoneMovies
import com.zephyr.remote.StreamCoordinator
import com.zephyr.remote.ZephyrClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** File origin for the Movies screen: PC = the existing
 * PC-folder behavior; Phone = browse the assigned Android folder and stream to
 * the Zephyr Player. File-level (not local) because the K2 compiler crashes on
 * enum-entry access from nested Compose lambdas over a function-local enum. */
enum class Origin(val label: String) { PC("PC"), PHONE("Phone") }

/**
 * The movies folder picked in Settings, on the TV-couch model: it opens there and never
 * climbs above it. Touch only: tap a folder to open it, tap a movie to play it,
 * "Up one folder" walks back out but never above the assigned folder.
 */
@Composable
fun MoviesScreen(remote: RemoteState, listState: LazyListState, onBack: () -> Unit, demoResumeAsk: Boolean = false, demoPhone: Boolean = false) {
    val scope = rememberCoroutineScope()
    val root = remote.store.moviesPath.trim()
    var path by remember { mutableStateOf(root) }
    var items by remember { mutableStateOf<List<BrowseItem>>(emptyList()) }
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(true) }
    // Where each folder was scrolled to before we walked into a child, so ".." lands
    // you back on the folder you came from instead of the top of the list.
    val scrollStack = remember { mutableStateListOf<Triple<String, Int, Int>>() }

    var origin by remember { mutableStateOf(if (demoPhone && BuildConfig.DEBUG) Origin.PHONE else Origin.PC) }
    val context = LocalContext.current
    // Asked ONCE at the first phone stream on Android 13+ so the stream service's
    // Stop action shows (teardown C1); the flag lives in the store, never nags again.
    val notifPermLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    fun askStreamNotifPermissionOnce() {
        if (android.os.Build.VERSION.SDK_INT < 33) return
        if (remote.store.notifPermissionAsked) return
        remote.store.notifPermissionAsked = true
        if (androidx.core.content.ContextCompat.checkSelfPermission(context, android.Manifest.permission.POST_NOTIFICATIONS)
            != android.content.pm.PackageManager.PERMISSION_GRANTED) {
            notifPermLauncher.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        }
    }
    // Stack of (SAF folder document URI, display name) below the assigned tree root
    // (SAF has no parent lookup, so the stack is how "up" and the breadcrumb work).
    val phoneStack = remember { mutableStateListOf<Pair<Uri, String>>() }
    // Movie doc-uri string -> its sidecar subtitle URIs (same folder, matching base name).
    var phoneSubs by remember { mutableStateOf<Map<String, List<Uri>>>(emptyMap()) }

    fun phoneTree(): Uri? = remote.store.moviesPhoneTreeUri.takeIf { it.isNotBlank() }?.let(Uri::parse)

    // VLC closed? The helper (always in the tray) lists and plays instead. In helper mode
    // paths are relative to the movies folder.
    var viaHelper by remember { mutableStateOf(false) }
    // PC origin browsing the Zephyr Player's /library instead (selected source).
    var viaZephyr by remember { mutableStateOf(false) }
    // The player's OWN at-root flag: its paths are relative, so they can never be
    // compared to the (absolute) VLC folder (teardown C4).
    var zephyrAtRoot by remember { mutableStateOf(true) }
    var zephyrFolder: String? = null
    // In-flight guard: a double-tap must not start two movies (two VLCs, two soundtracks).
    var starting by remember { mutableStateOf(false) }
    // The PC found a saved place in this movie: offer Resume / Start over (Preview 1).
    var resumeAsk by remember {
        mutableStateOf(
            // CI screenshot state (debug builds only): the dialog as he'd see it.
            if (demoResumeAsk && BuildConfig.DEBUG) ResumeAsk("demo.mkv", 34 * 60 + 12, 110 * 60 + 24) else null,
        )
    }

    /** Ask the PC to play a movies-folder file. resume: null = ask if a saved place
     * exists; true = resume; false = start over. */
    fun startPlay(relPath: String, resume: Boolean?) {
        if (starting) return
        starting = true
        scope.launch(Dispatchers.IO) {
            try {
                val resp = HelperClient.playMovie(
                    remote.store.host, remote.store.helperPort, remote.store.helperToken, relPath,
                    remote.store.vlcPort, remote.store.vlcPassword, fullscreen = true, resume = resume,
                )
                val ask = resp.optJSONObject("ask")
                if (ask != null) {
                    withContext(Dispatchers.Main) {
                        resumeAsk = ResumeAsk(relPath, ask.optLong("position", 0), ask.optLong("length", 0))
                    }
                } else {
                    remote.expectVlcStarting()
                    remote.retry()
                    withContext(Dispatchers.Main) { onBack() }
                }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) { error = e.message ?: "The PC could not start the movie." }
            } finally { starting = false }
        }
    }

    fun atRoot(p: String) =
        if (viaZephyr) zephyrAtRoot
        else if (viaHelper) p.isEmpty()
        else root.isNotEmpty() && p.trimEnd('\\', '/').equals(root.trimEnd('\\', '/'), ignoreCase = true)

    fun show(raw: List<BrowseItem>, dir: String, helperAtRoot: Boolean, restore: Pair<Int, Int>? = null) {
        // Locked to the assigned folder: no "up" once we are standing on it. The helper's
        // listing has no ".." row of its own, so make one from the relative path.
        val up = when {
            atRoot(dir) || helperAtRoot -> emptyList()
            // The Zephyr player's list has no ".." rows: make one from the relative path,
            // like the helper path does.
            viaHelper || viaZephyr -> listOf(BrowseItem(name = "..", isDir = true, path = dir.substringBeforeLast('\\', ""), uri = "", sizeBytes = 0L))
            else -> raw.filter { it.name == ".." }
        }
        val dirs = raw.filter { it.isDir && it.name != ".." }
        val files = raw.filter { !it.isDir && isVideoFile(it.name) }
        items = up + dirs + files
        path = dir
        loading = false
        scope.launch {
            if (restore != null) listState.scrollToItem(restore.first, restore.second)
            else listState.scrollToItem(0)
        }
    }

    /** Absolute VLC path -> helper path relative to the movies folder. */
    fun toRel(dir: String): String {
        val root = remote.store.moviesPath.trim().trimEnd('\\', '/')
        if (root.isEmpty()) return ""
        val d = dir.trimEnd('\\', '/')
        return when {
            d.equals(root, ignoreCase = true) -> ""
            d.startsWith(root, ignoreCase = true) -> d.substring(root.length).trimStart('\\', '/')
            else -> ""
        }
    }

    /** Helper-relative path -> absolute VLC path. */
    fun toAbs(rel: String): String {
        val root = remote.store.moviesPath.trim().trimEnd('\\', '/')
        return if (rel.isEmpty()) root else root + "\\" + rel
    }

    fun loadPhone(restore: Pair<Int, Int>? = null) {
        if (demoPhone && BuildConfig.DEBUG) {
            // CI screenshot state only: fake phone listing, no SAF access.
            items = listOf(
                BrowseItem(name = "Documentaries", isDir = true, path = "demo://d1", uri = "demo://d1", sizeBytes = 0L),
                BrowseItem(name = "The Big Example Movie (2024).mkv", isDir = false, path = "demo://m1", uri = "demo://m1", sizeBytes = 4_280_000_000L),
                BrowseItem(name = "Another Sample Film.mp4", isDir = false, path = "demo://m2", uri = "demo://m2", sizeBytes = 1_640_000_000L),
            )
            loading = false
            error = null
            return
        }

        val tree = phoneTree()
        if (tree == null) {
            loading = false
            items = emptyList()
            error = "Pick the Android movies folder first."
            return
        }
        loading = true
        error = null
        scope.launch {
            try {
                val folder = phoneStack.lastOrNull()?.first
                    ?: PhoneMovies.childUri(tree, android.provider.DocumentsContract.getTreeDocumentId(tree))
                val listing = withContext(Dispatchers.IO) { PhoneMovies.list(context, tree, folder) }
                val rows = mutableListOf<BrowseItem>()
                if (!listing.atRoot) rows.add(BrowseItem(name = "..", isDir = true, path = "", uri = "", sizeBytes = 0L))
                listing.folders.forEach { f -> rows.add(BrowseItem(name = f.name, isDir = true, path = f.uri.toString(), uri = f.uri.toString(), sizeBytes = 0L)) }
                listing.movies.forEach { m -> rows.add(BrowseItem(name = m.name, isDir = false, path = m.uri.toString(), uri = m.uri.toString(), sizeBytes = m.sizeBytes)) }
                phoneSubs = listing.movies.associate { it.uri.toString() to it.subtitles }
                items = rows
                loading = false
                scope.launch { if (restore != null) listState.scrollToItem(restore.first, restore.second) else listState.scrollToItem(0) }
            } catch (e: Exception) {
                loading = false
                error = plainPlayerError(e, "Couldn't open that folder on the phone.")
            }
        }
    }

    /** Phone-origin tap on a movie: serve it and hand the URL to the SELECTED
     *  target. Zephyr PC takes the Zephyr play call; VLC PC gets the same URL
     *  through VLC's in_play. No silent fallback between targets.
     *  Sidecar subtitles ride as :input-slave options on the VLC route. */
    fun streamPlay(item: BrowseItem) {
        if (starting) return
        askStreamNotifPermissionOnce()
        val toZephyr = remote.store.selectedSource == Source.ZEPHYR
        if (toZephyr && remote.store.zephyrToken.isBlank()) {
            error = "Not paired with the Zephyr Player yet."
            return
        }
        starting = true
        scope.launch(Dispatchers.IO) {
            try {
                val bound = StreamCoordinator.bind(context)
                try {
                    val movieUri = Uri.parse(item.uri)
                    val (url, subs) = StreamCoordinator.handoff(bound, movieUri, item.name, phoneSubs[item.uri] ?: emptyList())
                    StreamTarget.play(remote.store, url, subs)
                } finally {
                    StreamCoordinator.release(context, bound)
                }
                withContext(Dispatchers.Main) { onBack() }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) { error = plainPlayerError(e, "Couldn't start the stream.") }
            } finally { starting = false }
        }
    }

    fun loadVlc(dir: String, restore: Pair<Int, Int>?) {
        loading = true
        error = null
        scope.launch {
            try {
                val raw = withContext(Dispatchers.IO) {
                    Vlc.browse(remote.store.host, remote.store.vlcPort, remote.store.vlcPassword, dir)
                }
                show(raw, dir, false, restore)
            } catch (e: Exception) {
                loading = false
                // Only a dead VLC/PC shows the connection message; a VLC error (bad folder,
                // VLC itself upset) is shown where he is - no mode flip, no jump to the top.
                error = if (e is Vlc.Unreachable)
                    "Couldn't open that folder. Check the Movies folder in Settings, and that the PC is on."
                else e.message ?: "Couldn't open that folder."
            }
        }
    }

    fun loadHelper(rel: String, restore: Pair<Int, Int>? = null, healed: Boolean = false) {
        loading = true
        error = null
        scope.launch {
            try {
                val raw = withContext(Dispatchers.IO) {
                    HelperClient.listMovies(remote.store.host, remote.store.helperPort, remote.store.helperToken, rel)
                }
                val rows = raw.map { (name, dir) ->
                    BrowseItem(name = name, isDir = dir, path = if (rel.isEmpty()) name else "$rel\\$name", uri = "", sizeBytes = 0L)
                }
                show(rows, rel, rel.isEmpty(), restore)
            } catch (e: Exception) {
                // Self-heal once: the PC may have lost or never received the folder. If the
                // PC's copy is empty or different, re-save the phone's copy and retry.
                if (!healed && remote.store.helperToken.isNotBlank() && remote.store.moviesPath.isNotBlank()) {
                    val fixed = withContext(Dispatchers.IO) {
                        try {
                            val onPc = HelperClient.getMoviesFolder(remote.store.host, remote.store.helperPort, remote.store.helperToken)
                            if (onPc.isNullOrBlank() || !onPc.trim().equals(remote.store.moviesPath.trim(), ignoreCase = true)) {
                                HelperClient.setMoviesFolder(remote.store.host, remote.store.helperPort, remote.store.helperToken, remote.store.moviesPath.trim())
                                true
                            } else false
                        } catch (e2: Exception) { false }
                    }
                    if (fixed) { loadHelper(rel, restore, healed = true); return@launch }
                }
                if (e is HelperClient.ConnectTimeout) {
                    // The PC app can't answer at all - fall back to VLC for this listing.
                    viaHelper = false
                    loadVlc(toAbs(rel), restore)
                    return@launch
                }
                loading = false
                error = e.message?.takeIf { e is HelperClient.Unreachable } ?: "Couldn't open that folder on the PC."
            }
        }
    }

    /** PC origin with Zephyr as the selected source: browse the player's /library. */
    fun loadZephyr(rel: String, restore: Pair<Int, Int>? = null) {
        if (remote.store.zephyrToken.isBlank()) {
            loading = false
            error = "Zephyr PC isn't paired yet - pair it in Settings > Connected Sources."
            return
        }
        loading = true
        error = null
        scope.launch {
            try {
                val zHost = remote.store.zephyrHost.ifBlank { remote.store.host }
                val (atRootZ, rows) = withContext(Dispatchers.IO) {
                    ZephyrClient.libraryList(zHost, remote.store.zephyrPort, remote.store.zephyrToken, rel)
                }
                zephyrAtRoot = atRootZ
                show(rows.map { (name, dir) ->
                    BrowseItem(name = name, isDir = dir, path = if (rel.isEmpty()) name else "$rel\\$name", uri = "", sizeBytes = 0L)
                }, rel, atRootZ, restore)
            } catch (e: Exception) {
                loading = false
                error = plainPlayerError(e, "Couldn't open that folder on the Zephyr Player.")
            }
        }
    }

    /** PC-origin file tap with Zephyr selected: the player opens its own local file. */
    fun startZephyrPlay(rel: String) {
        if (starting) return
        starting = true
        scope.launch(Dispatchers.IO) {
            try {
                val zHost = remote.store.zephyrHost.ifBlank { remote.store.host }
                if (zephyrFolder == null) zephyrFolder = ZephyrClient.libraryFolder(zHost, remote.store.zephyrPort, remote.store.zephyrToken)
                val folder = zephyrFolder.orEmpty().trimEnd('\\')
                if (folder.isBlank()) throw java.io.IOException("No movies folder picked on the Zephyr Player yet.")
                val abs = (folder + "\\" + rel).replace('\\', '/')
                val uri = "file:///" + android.net.Uri.encode(abs, "/:")
                ZephyrClient.play(zHost, remote.store.zephyrPort, remote.store.zephyrToken, uri, emptyList())
                withContext(Dispatchers.Main) { onBack() }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) { error = plainPlayerError(e, "Couldn't play that one.") }
            } finally { starting = false }
        }
    }

    fun load(dir: String, restore: Pair<Int, Int>? = null) {
        // Selected-source routing: with Zephyr selected the PC origin IS the player.
        if (remote.store.selectedSource == Source.ZEPHYR) {
            viaHelper = false
            viaZephyr = true
            loadZephyr(toRel(dir), restore)
            return
        }
        viaZephyr = false
        if (viaHelper) { loadHelper(dir); return }
        // The PC app first (it answers instantly whether VLC is open or closed - the same
        // order as the Settings folder picker); VLC is only the fallback. This skips the
        // 4-second wait on a closed VLC that made the tab feel slow.
        if (remote.store.helperToken.isNotBlank()) {
            viaHelper = true
            loadHelper(toRel(dir), restore)
            return
        }
        loadVlc(dir, restore)
    }

    fun switchOrigin(o: Origin) {
        if (origin == o) return
        origin = o
        error = null
        if (o == Origin.PHONE) loadPhone() else { loading = true; load(path) }
    }

    val pickTree = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            runCatching {
                context.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            remote.store.moviesPhoneTreeUri = uri.toString()
            phoneStack.clear()
            origin = Origin.PHONE
            items = emptyList()
            error = null
            loadPhone()
        }
    }

    fun upOne() {
        fun popPos(forPath: String): Pair<Int, Int>? {
            val top = scrollStack.lastOrNull() ?: return null
            if (top.first != forPath) return null
            scrollStack.removeAt(scrollStack.size - 1)
            return top.second to top.third
        }
        if (viaZephyr) {
            val parent = path.substringBeforeLast('\\', "")
            loadZephyr(parent, popPos(parent))
            return
        }
        if (viaHelper) {
            val parent = path.substringBeforeLast('\\', "")
            loadHelper(parent, popPos(parent))
            return
        }
        val parent = path.trimEnd('\\', '/').substringBeforeLast('\\', "")
        if (parent.isNotBlank()) load(parent, popPos(parent))
    }

    fun open(item: BrowseItem) {
        if (origin == Origin.PHONE) {
            when {
                item.name == ".." -> {
                    if (phoneStack.isNotEmpty()) phoneStack.removeAt(phoneStack.size - 1)
                    loadPhone()
                }
                item.isDir -> {
                    phoneStack.add(Uri.parse(item.uri) to item.name)
                    loadPhone()
                }
                else -> streamPlay(item)
            }
            return
        }
        when {
            item.isDir -> {
                if (item.name == "..") {
                    // Walking up: drop the spot we saved for this folder's parent on the
                    // way down and land back on it, instead of jumping to the top.
                    val top = scrollStack.lastOrNull()
                    val restore = if (top != null && top.first == item.path) {
                        scrollStack.removeAt(scrollStack.size - 1)
                        top.second to top.third
                    } else null
                    if (viaZephyr) loadZephyr(item.path, restore) else if (viaHelper) loadHelper(item.path, restore) else load(item.path, restore)
                } else {
                    scrollStack.add(Triple(path, listState.firstVisibleItemIndex, listState.firstVisibleItemScrollOffset))
                    if (viaZephyr) loadZephyr(item.path) else if (viaHelper) loadHelper(item.path) else load(item.path)
                }
            }
            viaZephyr -> startZephyrPlay(item.path)
            viaHelper -> startPlay(item.path, null)
            else -> {
                if (starting) return
                starting = true
                scope.launch(Dispatchers.IO) {
                    try {
                        Vlc.playUri(remote.store.host, remote.store.vlcPort, remote.store.vlcPassword, item.uri)
                        withContext(Dispatchers.Main) { onBack() }
                    } catch (e: Exception) {
                        // Used to fail silently and just close - stay put and say it didn't play.
                        withContext(Dispatchers.Main) { error = "Couldn't play that one." }
                    } finally { starting = false }
                }
            }
        }
    }

    // The PC's setting wins when the helper has one; the phone's copy is the fallback.
    LaunchedEffect(Unit) {
        val fromPc = withContext(Dispatchers.IO) {
            if (remote.store.helperToken.isBlank()) null
            else HelperClient.getMoviesFolder(remote.store.host, remote.store.helperPort, remote.store.helperToken)
        }
        if (!fromPc.isNullOrBlank()) remote.store.moviesPath = fromPc
        if (demoPhone && BuildConfig.DEBUG) loadPhone() else load(remote.store.moviesPath.trim().ifBlank { path })
    }

    // Inside a folder, Back goes up one level first (never above the assigned folder).
    val insidePhoneFolder = origin == Origin.PHONE && phoneStack.isNotEmpty()
    BackHandler(enabled = insidePhoneFolder) {
        phoneStack.removeAt(phoneStack.size - 1)
        loadPhone()
    }
    val insideFolder = origin == Origin.PC && if (viaZephyr) !zephyrAtRoot else root.isNotEmpty() && !atRoot(path)
    BackHandler(enabled = insideFolder) { upOne() }
    BackHandler(enabled = !insideFolder) { onBack() }

    Column(Modifier.fillMaxSize().background(Bg)) {
        Row(
            // Same 16dp top as Settings, so the header doesn't jump 4dp between screens.
            Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            HeaderIconButton(onBack, remote.store.haptics) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back", tint = TextC) }
            Spacer(Modifier.width(12.dp))
            Text("Movies", color = TextC, fontSize = 20.sp, fontWeight = FontWeight.Bold)
        }
        Text(
            if (origin == Origin.PHONE)
                phoneFolderDisplay(remote.store.moviesPhoneTreeUri) + phoneStack.joinToString("") { " › " + it.second }
            else prettyPath(path),
            color = Muted, fontSize = 13.sp,
            modifier = Modifier.padding(horizontal = 16.dp),
        )
        Spacer(Modifier.height(10.dp))
        // Origin selector: orange = active origin (dark text), muted = inactive.
        Row(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp)
                .clip(RoundedCornerShape(12.dp))
                .background(Card),
        ) {
            listOf(Origin.PC, Origin.PHONE).forEach { o ->
                val sel = origin == o
                Box(
                    Modifier
                        .weight(1f)
                        .clip(RoundedCornerShape(12.dp))
                        .background(if (sel) OrangeGradient else androidx.compose.ui.graphics.Brush.linearGradient(listOf(Card, Card)))
                        .clickable { switchOrigin(o) }
                        .padding(vertical = 10.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(o.label, color = if (sel) Bg else Muted, fontSize = 15.sp, fontWeight = FontWeight.Bold)
                }
            }
        }
        Spacer(Modifier.height(10.dp))

        LazyColumn(
            state = listState,
            modifier = Modifier
                .weight(1f)
                .padding(horizontal = 16.dp),
        ) {
            when {
                loading -> item { ListMessage("Loading\u2026", loading = true) }
                error != null -> item {
                    if (origin == Origin.PHONE && phoneTree() == null)
                        ListMessage(error!!, isError = true, actionLabel = "Pick folder", onAction = { pickTree.launch(DebugRig.pickerInitialUri) })
                    else
                        ListMessage(error!!, isError = true, actionLabel = "Try again", onAction = { if (origin == Origin.PHONE) loadPhone() else load(path) })
                }
                items.isEmpty() -> item { ListMessage("No videos in this folder.") }
                else -> items(items.size, key = { it }) { i ->
                    val item = items[i]
                    PressTile(
                        onTap = { open(item) },
                        background = CardGradient,
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(vertical = 5.dp),
                        corner = 16.dp,
                    ) {
                        BrowseRowContent(
                            name = if (item.name == "..") "Up one folder" else if (item.isDir) item.name else displayName(item.name),
                            kind = when {
                                item.name == ".." -> BrowseKind.Up
                                item.isDir -> BrowseKind.Folder
                                else -> BrowseKind.Movie
                            },
                            detail = if (!item.isDir && item.sizeBytes > 0) formatSize(item.sizeBytes) else null,
                        )
                    }
                }
            }
        }

        Spacer(Modifier.height(12.dp))
    }
    resumeAsk?.let { ask ->
        androidx.compose.material3.AlertDialog(
            onDismissRequest = { resumeAsk = null },
            title = { Text("Keep watching?", color = TextC, fontWeight = FontWeight.Bold) },
            text = {
                Text(
                    "You're ${fmtClock(ask.positionSec)} in (of ${fmtClock(ask.lengthSec)}).",
                    color = TextC,
                )
            },
            confirmButton = {
                androidx.compose.material3.TextButton(onClick = {
                    resumeAsk = null
                    startPlay(ask.path, true)
                }) { Text("Resume", color = Orange, fontWeight = FontWeight.Bold) }
            },
            dismissButton = {
                androidx.compose.material3.TextButton(onClick = {
                    resumeAsk = null
                    startPlay(ask.path, false)
                }) { Text("Start over", color = Muted) }
            },
            containerColor = Card,
        )
    }
}



/** The saved place the PC reported for a movie. */
private data class ResumeAsk(val path: String, val positionSec: Long, val lengthSec: Long)

/** 34:12 or 1:50:24. */
private fun fmtClock(sec: Long): String {
    val h = sec / 3600; val m = (sec % 3600) / 60; val ss = sec % 60
    return if (h > 0) "%d:%02d:%02d".format(h, m, ss) else "%d:%02d".format(m, ss)
}

/** "C:\Users\you\Desktop\Movies" -> "Desktop > Movies" for the path line. */
fun prettyPath(path: String): String {
    val parts = path.replace('/', '\\').split('\\').filter { it.isNotBlank() && !it.endsWith(":") }
    return parts.takeLast(2).joinToString(" \u203A ").ifBlank { path }
}



/** Teardown C5: raw "Player returned N" plumbing becomes one plain sentence;
 *  already-plain messages (pairing errors, BadToken) pass through untouched. */
internal fun plainPlayerError(e: Exception, fallback: String): String {
    val m = e.message ?: return fallback
    if (m.startsWith("Player returned")) return "Zephyr Player had a problem. Try again."
    // Raw plumbing never reaches the screen (S2): socket text, timeouts and the
    // stream server's "server not ready" each get one plain sentence.
    if (m.contains("server not ready", true) || m.contains("no wi-fi", true) || m.contains("network is unreachable", true))
        return "Your phone needs to be on Wi-Fi to play from it."
    if (m.contains("failed to connect", true) || m.contains("connection refused", true)
        || m.contains("connect timed out", true) || m.contains("timeout", true)
        || m.contains("unable to resolve host", true))
        return "Can't reach the PC. Check it's on and on the same Wi-Fi, then try again."
    if (m.startsWith("{") || m.contains("Json", false) || m.contains("JSON")) return fallback
    return m
}
