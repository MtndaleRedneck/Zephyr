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
                    Forget(owner, key); // a press that passes drops older memory so nothing stale eats a later release
                    return false;
                }
                return Forget(owner, key);
            }
        }

        private bool Forget(string owner, string key)
        {
            if (!_swallowedDowns.TryGetValue(owner, out var set) || !set.Remove(key)) return false;
            if (set.Count == 0) _swallowedDowns.Remove(owner);
            return true;
        }

        public int SwallowedButtonCountForTests { get { lock (_gate) { var n = 0; foreach (var s in _swallowedDowns.Values) n += s.Count; return n; } } }
    }
}
