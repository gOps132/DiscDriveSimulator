namespace DiscDriveSimulator.Services;

/// <summary>xoshiro128** — a small, fast, non-cryptographic PRNG.</summary>
/// <remarks>
/// The hot loop pulls millions of doubles, where <see cref="Random"/> pays virtual dispatch
/// per call. A struct with inlined arithmetic avoids that.
/// <para>
/// <b>Not</b> reproducible across platforms: this only feeds a simulator whose results are
/// distributional, never cryptographic or seed-replayable.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Uniform double in <c>[0, 1)</c>, 32-bit resolution.
    /// </summary>
    /// <remarks>
    /// The only consumer is a binary search over a ≤5-entry CDF, so the mantissa bits a
    /// 53-bit conversion would add are never resolved.
    /// </remarks>
    public double NextDouble() => NextUInt32() * (1.0 / 4294967296.0);

    /// <summary>Uniform integer in <c>[0, maxExclusive)</c>.</summary>
    public int Next(int maxExclusive) => Next(0, maxExclusive);

    /// <summary>Uniform integer in <c>[minInclusive, maxExclusive)</c>.</summary>
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