namespace SecureLan;

/// <summary>Fixed 64-byte-max discovery frames (spec section 4). Probe: magic4 version1 nonce16 = 21 bytes. Reply: probe layout plus port2 = 23 bytes, nonce echoed.</summary>
public static class DiscoveryFrame
{
    public const byte Version = 1;
    public const int NonceLen = 16;
    public const int ProbeLen = 4 + 1 + NonceLen;
    public const int ReplyLen = ProbeLen + 2;

    public static byte[] BuildProbe(ReadOnlySpan<byte> magic, ReadOnlySpan<byte> nonce)
    {
        if (magic.Length != 4 || nonce.Length != NonceLen) throw new ArgumentException("size");
        var b = new byte[ProbeLen];
        magic.CopyTo(b); b[4] = Version; nonce.CopyTo(b.AsSpan(5));
        return b;
    }

    public static bool TryParseProbe(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> magic, out byte[] nonce)
    {
        nonce = Array.Empty<byte>();
        if (packet.Length != ProbeLen || !packet[..4].SequenceEqual(magic) || packet[4] != Version) return false;
        nonce = packet.Slice(5, NonceLen).ToArray();
        return true;
    }

    public static byte[] BuildReply(ReadOnlySpan<byte> replyMagic, ReadOnlySpan<byte> nonce, ushort tlsPort)
    {
        if (replyMagic.Length != 4 || nonce.Length != NonceLen) throw new ArgumentException("size");
        var b = new byte[ReplyLen];
        replyMagic.CopyTo(b); b[4] = Version; nonce.CopyTo(b.AsSpan(5));
        b[21] = (byte)(tlsPort >> 8); b[22] = (byte)tlsPort;
        return b;
    }

    /// <summary>Accepts only an exact-length reply with the expected magic, version, echoed nonce and a nonzero port.</summary>
    public static bool TryParseReply(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> replyMagic, ReadOnlySpan<byte> expectedNonce, out ushort port)
    {
        port = 0;
        if (packet.Length != ReplyLen || !packet[..4].SequenceEqual(replyMagic) || packet[4] != Version) return false;
        if (!packet.Slice(5, NonceLen).SequenceEqual(expectedNonce)) return false;
        port = (ushort)((packet[21] << 8) | packet[22]);
        return port != 0;
    }
}
