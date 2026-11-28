package com.bingemote

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

/**
 * One atomic admission for helper rediscovery, shared by every caller (poll loop and mouse screen go through the same RemoteState).
 * It covers the in-flight flag and the minimum gap together under one lock, so two callers cannot both be admitted. The gap starts
 * when an attempt is admitted.
 *
 * Release has exactly ONE owner: the completion handler of the launched job ([launchIfAdmitted]). A completion handler runs once per
 * job, including a job cancelled before its body ever started, so the flag cannot wedge. Nothing else releases (no release in the
 * body), so an old job can never clear a newer admission: a new attempt is only admitted after the previous job completed.
 */
class RediscoverAdmission(private val clock: () -> Long = { System.currentTimeMillis() }, private val minGapMs: Long = 60_000L) {
    private var inFlight = false
    private var lastAdmittedAt: Long? = null

    @Synchronized fun tryAdmit(): Boolean {
        val now = clock()
        if (inFlight) return false
        val last = lastAdmittedAt
        if (last != null && now - last < minGapMs) return false
        inFlight = true
        lastAdmittedAt = now
        return true
    }

    @Synchronized internal fun release() { inFlight = false }

    /** Admits, launches [block] (always the default start: a never-started lazy job would never complete and would wedge the flag), and releases when the job completes or is cancelled, even before it starts. Null when not admitted. */
    fun launchIfAdmitted(
        scope: CoroutineScope, context: CoroutineContext = EmptyCoroutineContext,
        block: suspend CoroutineScope.() -> Unit,
    ): Job? {
        if (!tryAdmit()) return null
        val job = try { scope.launch(context, block = block) } catch (e: Throwable) { release(); throw e }
        job.invokeOnCompletion { release() }
        return job
    }
}
