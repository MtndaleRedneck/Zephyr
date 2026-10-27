package com.bingemote

import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/** Round-6 source guard: the pairing credentials and endpoint fields (zephyrHost,
 *  zephyrToken, helperToken, host) may only ever be WRITTEN by the two setup screens,
 *  plus the debug capture rig in MainActivity (comment-marked, demo values). Any new
 *  writer could attach a token to an address that never proved itself by pairing
 *  (S11 / N-3), so this test fails the build if one appears. */
class SourceGuardTest {

    @Test fun `pairing credential writes only come from the setup screens`() {
        val root = File("src/main/java")
        assertTrue("test must run from the android module dir", root.isDirectory)
        val assign = Regex("""\b(store|remote\.store|s)\.(zephyrHost|zephyrToken|helperToken|host)\s*=(?!=)""")
        val allowed = setOf("SetupScreen.kt", "ZephyrSetupScreen.kt", "MainActivity.kt")
        val offenders = mutableListOf<String>()
        root.walkTopDown().filter { it.extension == "kt" }.forEach { f ->
            f.useLines { lines ->
                lines.forEachIndexed { i, line ->
                    if (line.trimStart().startsWith("//")) return@forEachIndexed
                    if (assign.containsMatchIn(line) && f.name !in allowed)
                        offenders += "${f.name}:${i + 1}: ${line.trim()}"
                }
            }
        }
        assertTrue("unexpected pairing-credential writers:\n" + offenders.joinToString("\n"), offenders.isEmpty())
    }
}
