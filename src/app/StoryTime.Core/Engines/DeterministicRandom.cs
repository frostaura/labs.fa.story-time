namespace StoryTime.Core.Engines;

/// <summary>Small xorshift PRNG — identical sequence on every platform/runtime, unlike
/// System.Random whose algorithm is not contractually stable. The template engine's titles
/// and prose choices are pinned to this exact sequence, so it must never change.</summary>
internal sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(int seed) => _state = (ulong)(seed == 0 ? 88172645463325252L : seed) * 2685821657736338717UL + 1;

    public ulong NextULong()
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return _state;
    }

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public int Next(int maxExclusive) => (int)(NextULong() % (ulong)maxExclusive);
}
