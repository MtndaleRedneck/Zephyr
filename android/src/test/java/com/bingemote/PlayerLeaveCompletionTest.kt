package com.bingemote

import org.junit.Assert.assertEquals
import org.junit.Test

/** Cancel/Back after a Player pairing: when does leaving finish the Player-only first run? (Cancel and Back both call leave().) */
class PlayerLeaveCompletionTest {
    private fun f(setupDone: Boolean = false, vlc: String = "", player: String = "tok", saved: Boolean = true, ok: Boolean = true) =
        shouldFinishPlayerFirstRunOnLeave(setupDone, vlc, player, saved, ok)

    @Test fun validSavedPlayerTokenWithCheckOkOrCouldntCheckFinishes() { assertEquals(true, f(ok = true)) }
    @Test fun rejectedOrStillCheckingTokenDoesNotFinish() { assertEquals(false, f(ok = false)) }
    @Test fun blankPlayerTokenDoesNotFinish() { assertEquals(false, f(player = "")) }
    @Test fun nothingSavedThisSessionDoesNotFinish() { assertEquals(false, f(saved = false)) }
    @Test fun vlcPairingInProgressKeepsVlcAndSource() { assertEquals(false, f(vlc = "vlc-helper-token")) }
    @Test fun finishedSetupIsLeftAlone() { assertEquals(false, f(setupDone = true)) }
}
