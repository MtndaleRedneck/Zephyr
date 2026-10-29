using System;
using System.Collections.Generic;

namespace ZephyrPlayer;

// Per-token button ownership. Only the owner may renew/release a held button.
internal sealed class InputLease
{
    private readonly IInputSink _sink;
    private readonly object _gate = new();
    private bool _closed;
    private readonly Dictionary<string, (string owner, DateTime expires)> _held = new();
    internal InputLease(IInputSink sink) { _sink = sink; }
    internal void Button(string token, string button, bool down, DateTime now) {
        lock (_gate) {
            if (_closed) return;
            if (down) {
                if (_held.TryGetValue(button, out var old) && old.owner != token) return;
                _sink.Button(button, true); _held[button] = (token, now.AddSeconds(8));
            } else if (_held.TryGetValue(button, out var old) && old.owner == token) {
                _sink.Button(button, false); _held.Remove(button);
            }
        }
    }
    internal void Renew(string token, DateTime now) {
        lock (_gate) foreach (var b in new List<string>(_held.Keys)) if (_held[b].owner == token) _held[b] = (token, now.AddSeconds(8));
    }
    internal void Expire(DateTime now, Func<string, bool> valid) {
        lock (_gate) foreach (var b in new List<string>(_held.Keys)) if (_held[b].expires <= now || !valid(_held[b].owner)) { _sink.Button(b, false); _held.Remove(b); }
    }
    internal void Close() { lock (_gate) { _closed = true; ReleaseAll(); } }
    internal void ReleaseAll() { lock (_gate) { foreach (var b in _held.Keys) _sink.Button(b, false); _held.Clear(); } }
}
