namespace StoryTime.Poster.Core;

/// <summary>Deterministic PRNG (SplitMix64). Its algorithm is part of the poster contract:
/// System.Random's is not contractually stable across runtimes, this one is — the same
/// seed yields the same poster on the Mac, the simulator and every iPhone.</summary>
public sealed class Rng
{
    private ulong _state;

    public Rng(int seed) : this((ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ 0xD1B54A32D192ED03UL) { }

    public Rng(ulong state) => _state = state;

    /// <summary>A child stream: independent of how many draws the parent made later, so
    /// adding a feature to one layer never reshuffles another layer's geometry.</summary>
    public Rng Fork(string label) => new(Hash.Combine(_state, Hash.Of(label)));

    public ulong NextULong()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform [0,1).</summary>
    public double Next() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public double Range(double min, double max) => min + (max - min) * Next();

    public int Int(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(NextULong() % (ulong)maxExclusive);

    public int Int(int min, int maxInclusive) => min + Int(maxInclusive - min + 1);

    public bool Chance(double p) => Next() < p;

    /// <summary>Symmetric jitter in [-amount, amount].</summary>
    public double Jitter(double amount) => (Next() * 2 - 1) * amount;

    /// <summary>Triangular-ish bell in [-1,1] (sum of two uniforms) — gentle variation.</summary>
    public double Bell() => Next() + Next() - 1;

    public T Pick<T>(IReadOnlyList<T> items) => items[Int(items.Count)];

    public void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Int(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

/// <summary>Stable 64-bit hashing (FNV-1a + avalanche) for seeds and labels.</summary>
public static class Hash
{
    public static ulong Of(string text)
    {
        var h = 0xCBF29CE484222325UL;
        foreach (var ch in text)
        {
            h ^= ch;
            h *= 0x100000001B3UL;
        }
        return Mix(h);
    }

    public static ulong Combine(ulong a, ulong b) => Mix(a ^ (b + 0x9E3779B97F4A7C15UL + (a << 6) + (a >> 2)));

    public static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Integer lattice hash → [0,1). Used by the noise functions.</summary>
    public static double Unit(int x, int y, uint seed)
    {
        var h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ seed * 0xCB1AB31Fu;
        h ^= h >> 13;
        h *= 0x5BD1E995u;
        h ^= h >> 15;
        return h * (1.0 / 4294967296.0);
    }
}
