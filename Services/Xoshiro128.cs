namespace DiscDriveSimulator.Services;

// xoshiro128** — a small, fast, non-cryptographic PRNG.
//
// The hot loop pulls millions of doubles, where Random pays virtual dispatch per call.
// A struct with inlined arithmetic avoids that.
//
// Not reproducible across platforms: this only feeds a simulator whose results are
// distributional, never cryptographic or seed-replayable.
public struct Xoshiro128
{
    private uint _s0;
    private uint _s1;
    private uint _s2;
    private uint _s3;

    public Xoshiro128() : this(Random.Shared.Next(), Random.Shared.Next(),
                               Random.Shared.Next(), Random.Shared.Next())
    {
    }

    public Xoshiro128(int seed)
    {
        // SplitMix32 expansion: a small integer seed still fills all four words, so nearby
        // seeds do not produce correlated streams.
        uint x = (uint)seed;
        _s0 = SplitMix32(ref x);
        _s1 = SplitMix32(ref x);
        _s2 = SplitMix32(ref x);
        _s3 = SplitMix32(ref x);
    }

    public Xoshiro128(int a, int b, int c, int d)
    {
        // Guard against the all-zero state, which xoshiro cannot escape.
        if ((a | b | c | d) == 0) c = unchecked((int)0x9E3779B9);
        _s0 = (uint)a;
        _s1 = (uint)b;
        _s2 = (uint)c;
        _s3 = (uint)d;
    }

    private static uint SplitMix32(ref uint x)
    {
        x += 0x9E3779B9;
        uint z = x;
        z = (z ^ (z >> 16)) * 0x85EBCA6B;
        z = (z ^ (z >> 13)) * 0xC2B2AE35;
        return z ^ (z >> 16);
    }

    public uint NextUInt32()
    {
        // Safety net for the all-zero state, which xoshiro cannot escape: without this, a
        // default-constructed instance returns 0 on every draw and Next(min, max) rejects
        // forever. One OR plus a predictable branch per draw.
        if ((_s0 | _s1 | _s2 | _s3) == 0) _s3 = 0x9E3779B9;

        unchecked
        {
            uint result = Rotl(_s1 * 5, 7) * 9;
            uint t = _s1 << 9;

            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = Rotl(_s3, 11);

            return result;
        }
    }

    private static uint Rotl(uint x, int k) => (x << k) | (x >> (32 - k));

    // Uniform double in [0, 1), 32-bit resolution.
    // The only consumer is a binary search over a ≤5-entry CDF, so the mantissa bits a
    // 53-bit conversion would add are never resolved.
    public double NextDouble() => NextUInt32() * (1.0 / 4294967296.0);

    // Uniform integer in [0, maxExclusive).
    public int Next(int maxExclusive) => Next(0, maxExclusive);

    // Uniform integer in [minInclusive, maxExclusive).
    public int Next(int minInclusive, int maxExclusive)
    {
        uint range = (uint)(maxExclusive - minInclusive);
        if (range == 0) return minInclusive;

        // Lemire-style rejection. The threshold discards the short final bucket that plain
        // modulo would bias toward the low end of the range.
        uint threshold = unchecked((uint)(-range)) % range;
        while (true)
        {
            uint r = NextUInt32();
            if (r >= threshold) return minInclusive + (int)(r % range);
        }
    }
}