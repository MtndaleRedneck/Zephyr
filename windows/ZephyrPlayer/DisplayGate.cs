using System;
using System.Collections.Generic;

namespace ZephyrPlayer
{
    /// <summary>
    /// Wake-only first press (BingeMote helper 1.7.2 behavior, ported to the Player's own command
    /// paths). While the PC screen is dark the FIRST phone press only wakes the screen; the press's
    /// own action is swallowed so nothing fires blind. Discrete presses (keys, typing, playback
    /// commands, starting a movie) are gated on the dark check alone, so the next press after the
    /// wake works. Touchpad moves, scrolls and zooms have no gesture end in the protocol, so a
    /// swallowed gesture is eaten to its end: 400 ms of quiet hands control back. A mouse button
    /// press that was swallowed also swallows its matching release for the same phone and button
    /// (a lone right-up would open a context menu); every other release always passes.
    /// The state source is injected: production feeds it from the display power notification,
    /// tests feed it directly. Hardware behavior is only proven on a real PC.
    /// </summary>
    public sealed class DisplayGate
    {
        private readonly Func<bool> _isDarkOrUnknown;
        private readonly Action _wake;
        private readonly Func<DateTime> _now;
        private readonly object _gate = new();
        private DateTime _swallowUntil = DateTime.MinValue;
        private readonly Dictionary<string, HashSet<string>> _swallowedDowns = new();
        private readonly Dictionary<string, HashSet<string>> _deliveredDowns = new();

        public static readonly TimeSpan GestureQuiet = TimeSpan.FromMilliseconds(400);

        public DisplayGate(Func<bool> isDarkOrUnknown, Action wake, Func<DateTime>? now = null)
        {
            _isDarkOrUnknown = isDarkOrUnknown;
            _wake = wake;
            _now = now ?? (() => DateTime.UtcNow);
        }

        public bool DarkOrUnknown => _isDarkOrUnknown();

        /// <summary>One-shot wake request from the phone (fire-and-forget).</summary>
        public void Wake() { try { _wake(); } catch { } }

        /// <summary>Clicks-free presses: keys, typing, playback commands, starting a movie.
        /// True = consumed (screen woken), do not run the action.</summary>
        public bool ShouldSwallowDiscrete()
        {
            lock (_gate) return DiscreteLocked();
        }

        private bool DiscreteLocked()
        {
            if (!_isDarkOrUnknown()) return false;
            Wake();
            return true;
        }

        /// <summary>Touchpad moves, scrolls, zooms. True = frame consumed.</summary>
        public bool ShouldSwallowContinuous()
        {
            lock (_gate)
            {
                var now = _now();
                if (_isDarkOrUnknown())
                {
                    Wake();
                    _swallowUntil = now + GestureQuiet;
                    return true;
                }
                if (now < _swallowUntil)
                {
                    _swallowUntil = now + GestureQuiet;
                    return true;
                }
                return false;
            }
        }

        private static string Key(string? button) => button == "right" ? "right" : "left";

        /// <summary>A mouse button frame from one phone. True = do not inject it.</summary>
        public bool ShouldSwallowButton(string owner, string? button, bool down)
        {
            var key = Key(button);
            lock (_gate)
            {
                if (down)
                {
                    if (DiscreteLocked())
                    {
                        if (!_swallowedDowns.TryGetValue(owner, out var set)) _swallowedDowns[owner] = set = new HashSet<string>();
                        set.Add(key);
                        return true;
                    }
                    Forget(_swallowedDowns, owner, key); // a press that passes drops older memory so nothing stale eats a later release
                    if (!_deliveredDowns.TryGetValue(owner, out var del)) _deliveredDowns[owner] = del = new HashSet<string>();
                    del.Add(key);
                    return false;
                }
                // A press that was really delivered always gets its release, even if the screen went dark
                // in between or a second press from that phone was swallowed since (otherwise the OS
                // button would stay held while the phone keeps its heartbeat alive).
                if (Forget(_deliveredDowns, owner, key)) { Forget(_swallowedDowns, owner, key); return false; }
                return Forget(_swallowedDowns, owner, key);
            }
        }

        private static bool Forget(Dictionary<string, HashSet<string>> map, string owner, string key)
        {
            if (!map.TryGetValue(owner, out var set) || !set.Remove(key)) return false;
            if (set.Count == 0) map.Remove(owner);
            return true;
        }

        /// <summary>Forget memory for phones whose token is no longer valid (revoked). Called from the
        /// lease timer, so stale entries live at most one tick.</summary>
        public void Prune(Func<string, bool> valid)
        {
            List<string> owners;
            lock (_gate) { owners = new List<string>(_swallowedDowns.Keys); foreach (var k in _deliveredDowns.Keys) if (!owners.Contains(k)) owners.Add(k); }
            if (owners.Count == 0) return;
            // Validity is asked OUTSIDE the gate lock (the store may take its own lock or touch disk),
            // so a slow store never stalls input presses. The check must NOT mark the phone as seen.
            var gone = new List<string>();
            foreach (var o in owners) { bool ok; try { ok = valid(o); } catch { ok = true; } if (!ok) gone.Add(o); }
            if (gone.Count == 0) return;
            lock (_gate) foreach (var o in gone) { _swallowedDowns.Remove(o); _deliveredDowns.Remove(o); }
        }

        /// <summary>Drop all per-phone button memory (token revoked or lease expired).</summary>
        public void ForgetOwner(string owner)
        {
            lock (_gate) { _swallowedDowns.Remove(owner); _deliveredDowns.Remove(owner); }
        }

        public int SwallowedButtonCountForTests { get { lock (_gate) { var n = 0; foreach (var s in _swallowedDowns.Values) n += s.Count; return n; } } }
    }
}
