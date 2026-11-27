using System.Text;
namespace SecureLan;

/// <summary>Crockford base32 for the typed anchor and code (spec v3.6 section 5b). Canonical: nonzero pad bits are rejected.</summary>
public static class Crockford
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data, int chars)
    {
        int bits = data.Length * 8;
        if (chars * 5 < bits || chars * 5 - bits >= 5) throw new ArgumentException("char count does not fit data");
        var sb = new StringBuilder(chars);
        int acc = 0, nacc = 0, pos = 0;
        for (int i = 0; i < chars; i++)
        {
            while (nacc < 5)
            {
                acc = (acc << 8) | (pos < data.Length ? data[pos] : 0);
                pos++; nacc += 8;
            }
            nacc -= 5;
            sb.Append(Alphabet[(acc >> nacc) & 31]);
            acc &= (1 << nacc) - 1;
        }
        return sb.ToString();
    }

    /// <summary>Case-insensitive, strips spaces and dashes, O to 0, I and L to 1.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char raw in s)
        {
            char c = char.ToUpperInvariant(raw);
            if (c == ' ' || c == '-') continue;
            if (c == 'O') c = '0';
            else if (c == 'I' || c == 'L') c = '1';
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool TryDecodeSymbols(string normalized, out int[] symbols)
    {
        symbols = new int[normalized.Length];
        for (int i = 0; i < normalized.Length; i++)
        {
            int v = Alphabet.IndexOf(normalized[i]);
            if (v < 0) return false;
            symbols[i] = v;
        }
        return true;
    }

    /// <summary>Decode exactly byteLen bytes from chars symbols; rejects wrong length, bad symbols and nonzero pad bits.</summary>
    public static bool TryDecode(string normalized, int byteLen, out byte[] data)
    {
        data = Array.Empty<byte>();
        int chars = (byteLen * 8 + 4) / 5;
        if (normalized.Length != chars || !TryDecodeSymbols(normalized, out var sym)) return false;
        var outb = new byte[byteLen];
        int acc = 0, nacc = 0, pos = 0;
        foreach (int v in sym)
        {
            acc = (acc << 5) | v; nacc += 5;
            while (nacc >= 8 && pos < byteLen)
            {
                nacc -= 8;
                outb[pos++] = (byte)((acc >> nacc) & 0xFF);
                acc &= (1 << nacc) - 1;
            }
        }
        if (acc != 0) return false;   // leftover pad bits must be zero
        data = outb;
        return true;
    }
}

/// <summary>One whole-field linear check symbol over GF(2^5) (x^5+x^2+1). Typo detection only, not security.</summary>
public static class LinearCheck
{
    static int MulAlpha(int x)
    {
        x <<= 1;
        if ((x & 32) != 0) x ^= 0b100101;
        return x;
    }

    static int State(IEnumerable<int> symbols)
    {
        int s = 0;
        foreach (int d in symbols) s = MulAlpha(s ^ d);
        return s;
    }

    public static int Compute(int[] dataSymbols)
    {
        for (int c = 0; c < 32; c++)
        {
            if (State(dataSymbols.Append(c)) == 0) return c;
        }
        throw new InvalidOperationException("unreachable: step is a bijection");
    }

    public static string Append(string normalizedData)
    {
        if (!Crockford.TryDecodeSymbols(normalizedData, out var sym)) throw new ArgumentException("bad symbol");
        return normalizedData + Crockford.Alphabet[Compute(sym)];
    }

    public static bool Verify(string normalizedWithCheck) =>
        normalizedWithCheck.Length > 1 && Crockford.TryDecodeSymbols(normalizedWithCheck, out var sym) && State(sym) == 0;
}
