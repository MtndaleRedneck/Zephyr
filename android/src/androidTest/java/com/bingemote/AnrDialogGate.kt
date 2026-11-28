package com.bingemote

/**
 * Pure decision logic (no Android types) for the cold-open window proof. The emulator's Pixel Launcher can show a system
 * "isn't responding" dialog that takes focus and hides the app from UiAutomator even though the app is resumed and drawn.
 * Exactly that one dialog (this title, served by package "android") is tolerated and tapped; anything else fails the test.
 * The window criterion itself is NOT decided here: waitForWindow returns true only when the caller's own window query says so,
 * and returns false when the same budget runs out, whatever dialogs came and went.
 */
object AnrDialogGate {
    const val TOLERATED_TITLE = "Pixel Launcher isn't responding"
    const val TOLERATED_PACKAGE = "android"
    const val MAX_TAPS = 5

    enum class Verdict { TOLERATE, REJECT }

    fun classify(title: String?, pkg: String?): Verdict =
        if (title == TOLERATED_TITLE && pkg == TOLERATED_PACKAGE) Verdict.TOLERATE else Verdict.REJECT

    /** Tap count shared by the arrival wait and the persistence check of one test, so MAX_TAPS caps both together. */
    class Taps { var count = 0 }

    /** A dialog currently on screen: its text and the package that serves it, as UiAutomator reports them. */
    class Dialog(val title: String?, val pkg: String?)

    /**
     * Polls until windowUp(pollMs) is true or the budget is spent. Between polls: a tolerated dialog is captured (before the tap), then
     * tapped; a rejected one throws and is never tapped; more than MAX_TAPS taps throws. The deadline is never extended by a tap.
     */
    fun waitForWindow(
        budgetMs: Long,
        pollMs: Long,
        taps: Taps,
        now: () -> Long,
        dialog: () -> Dialog?,
        capture: (tapNumber: Int, d: Dialog) -> Unit,
        tap: () -> Unit,
        windowUp: (waitMs: Long) -> Boolean,
    ): Boolean {
        val end = now() + budgetMs
        do {
            val d = dialog()
            if (d != null) dismiss(d, taps, capture, tap)
            if (windowUp(pollMs)) return true
        } while (now() < end)
        return false
    }

    /**
     * Persistence check: the window query runs IMMEDIATELY, no waiting and no grace. Only when it fails AND a tolerated dialog is
     * actually on screen is that dialog captured and tapped and the query run again, repeating only while a dialog is present
     * (bounded by the shared MAX_TAPS). No dialog on screen means the first failed query is the answer.
     */
    fun persists(
        taps: Taps,
        dialog: () -> Dialog?,
        capture: (tapNumber: Int, d: Dialog) -> Unit,
        tap: () -> Unit,
        hasWindow: () -> Boolean,
    ): Boolean {
        while (true) {
            if (hasWindow()) return true
            val d = dialog() ?: return false
            dismiss(d, taps, capture, tap)
        }
    }

    private fun dismiss(d: Dialog, taps: Taps, capture: (Int, Dialog) -> Unit, tap: () -> Unit) {
        if (classify(d.title, d.pkg) == Verdict.REJECT) {
            throw AssertionError("unexpected ANR dialog during cold open, not dismissed: title=${d.title} pkg=${d.pkg}")
        }
        taps.count += 1
        if (taps.count > MAX_TAPS) throw AssertionError("Pixel Launcher ANR dialog keeps returning (taps=${taps.count})")
        capture(taps.count, d)
        tap()
    }
}
