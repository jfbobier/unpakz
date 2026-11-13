namespace Unpakz.Tests.Support;

/// <summary>Deterministic content kinds chosen to drive different compressor paths (literals, matches, filters).</summary>
internal static class TestData
{
    private static readonly string[] Words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"];

    public static byte[] Text(int size, int seed = 1)
    {
        var rng = new Random(seed);
        var b = new byte[size];
        var i = 0;
        while (i < size)
        {
            foreach (var c in Words[rng.Next(Words.Length)] + (rng.Next(10) == 0 ? ".\n" : " "))
            {
                if (i == size) break;
                b[i++] = (byte)c;
            }
        }

        return b;
    }

    public static byte[] Random(int size, int seed = 2)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Fixed-width records with slowly changing fields: the case delta filtering is made for.</summary>
    public static byte[] Table(int size, int seed = 3)
    {
        var rng = new Random(seed);
        var b = new byte[size];
        uint a = 1000, c = 50_000;
        for (var i = 0; i + 8 <= size; i += 8)
        {
            a += (uint)rng.Next(0, 4);
            c += (uint)rng.Next(0, 300);
            BitConverter.GetBytes(a).CopyTo(b, i);
            BitConverter.GetBytes(c).CopyTo(b, i + 4);
        }

        return b;
    }

    /// <summary>x86-looking code: plenty of CALL/JMP rel32 with plausible targets.</summary>
    public static byte[] X86(int size, int seed = 4)
    {
        var rng = new Random(seed);
        var b = new byte[size];
        for (var i = 0; i < size;)
        {
            var r = rng.Next(100);
            if (r < 14 && i + 5 <= size)
            {
                b[i] = (byte)(rng.Next(2) == 0 ? 0xE8 : 0xE9);
                var target = rng.Next(-60_000, 60_000);
                BitConverter.GetBytes(target).CopyTo(b, i + 1);
                i += 5;
            }
            else
            {
                b[i++] = (byte)rng.Next(0x40, 0x90);
            }
        }

        return b;
    }

    /// <summary>16-bit stereo "audio": two correlated slowly varying channels with noise.</summary>
    public static byte[] Audio(int size, int seed = 5)
    {
        var rng = new Random(seed);
        var b = new byte[size];
        for (var i = 0; i + 4 <= size; i += 4)
        {
            var t = i / 4.0;
            var l = (short)((Math.Sin(t / 40) * 9000) + rng.Next(-80, 80));
            var r = (short)((Math.Sin(t / 40 + 0.3) * 8000) + rng.Next(-80, 80));
            BitConverter.GetBytes(l).CopyTo(b, i);
            BitConverter.GetBytes(r).CopyTo(b, i + 2);
        }

        return b;
    }

    public static byte[] Zeros(int size) => new byte[size];

    /// <summary>Long-range repeats (a block repeated far apart): exercises large distances.</summary>
    public static byte[] LongRange(int size, int blockSize, int seed = 6)
    {
        var block = Random(blockSize, seed);
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = block[i % blockSize];
        return b;
    }
}
