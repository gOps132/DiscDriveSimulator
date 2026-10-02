using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

/// <summary>
/// Closed-form sampler for Roll Value, letting the fast path decide keep/dismantle without
/// materializing a <see cref="Disc"/>.
/// </summary>
/// <remarks>
/// <para>
/// RV is the count of drawn substats that are prioritized: every roll has exactly one hit and
/// nothing increments it. Substats are drawn as a shuffle-and-take-prefix, which draws
/// <i>distinct</i> pool elements — sampling without replacement. So with pool size <c>n</c>
/// after main-stat exclusion, <c>d</c> prioritized stats surviving in it, and <c>k</c> drawn:
///
/// <c>X ~ Hypergeometric(n, d, k)</c>, i.e. <c>P(X = x) = C(d,x)·C(n-d,k-x) / C(n,k)</c>.
/// </para>
/// <para>
/// The distribution depends only on the counts <c>(n, d, k)</c>, so it collapses to
/// precomputed tables and one binary search replaces the whole shuffle-and-count pipeline.
/// A dismantled drop then costs one uniform double.
/// </para>
/// <para>
/// <b>Traps.</b> Two subtleties are load-bearing and were both bugs first: the substat count
/// is itself random (3 or 4), so RV is a <i>mixture</i> over k; and a main stat only shrinks
/// the pool if it is a pool member, which slots 5 and 6 offer several that are not.
/// </para>
/// <para>
/// Tables are ratios of binomial coefficients in <see cref="double"/>, exact at
/// <c>n ≤ 10</c>, <c>k ≤ 4</c>. The guards throw rather than degrade: a silently-wrong
/// distribution is worse than a crash. Full derivation in
/// <c>docs/hypergeometric-sampling.md</c>.
/// </para>
/// </remarks>
public static class HypergeometricSampler
{
    /// <summary>Largest pool size for which the closed form stays exact in <see cref="double"/>.</summary>
    public const int MaxExactPoolSize = 20;

    /// <summary>Largest draw count for which the closed form stays exact in <see cref="double"/>.</summary>
    public const int MaxExactDrawCount = 8;

    /// <summary>
    /// Builds the probability mass function for <c>X ~ Hypergeometric(n, d, k)</c>.
    /// </summary>
    /// <param name="n">Pool size after excluding the disc's main stat.</param>
    /// <param name="d">Number of prioritized stats present in that pool.</param>
    /// <param name="k">Number of substats drawn (3 or 4 in this game).</param>
    /// <returns>
    /// A PMF indexed by <c>x</c>, with trailing zeros trimmed to <c>min(d, k) + 1</c>
    /// entries — RV can never exceed <c>min(d, k)</c>, so extra entries would only
    /// slow the sampling loop. The entries sum to exactly 1.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="n"/> exceeds <see cref="MaxExactPoolSize"/>,
    /// <paramref name="k"/> exceeds <see cref="MaxExactDrawCount"/>, <c>k &lt; 1</c>,
    /// or <c>d &gt; n</c>. These bounds keep the binomial ratios exact in a
    /// <see cref="double"/>; widening the substat pool or the max substat count would
    /// require a log-gamma based implementation instead.
    /// </exception>
    public static double[] BuildTable(int n, int d, int k)
    {
        if (n < 1)
            throw new ArgumentOutOfRangeException(nameof(n), n, "Pool size must be at least 1.");
        if (n > MaxExactPoolSize)
            throw new ArgumentOutOfRangeException(nameof(n), n,
                $"Pool size above {MaxExactPoolSize} breaks double-precision exactness of the closed form.");
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), k, "Must draw at least one substat.");
        if (k > MaxExactDrawCount)
            throw new ArgumentOutOfRangeException(nameof(k), k,
                $"Draw count above {MaxExactDrawCount} breaks double-precision exactness of the closed form.");
        if (d < 0)
            throw new ArgumentOutOfRangeException(nameof(d), d, "Desired count cannot be negative.");
        if (d > n)
            throw new ArgumentOutOfRangeException(nameof(d), d, $"Desired count cannot exceed pool size {n}.");
        if (k > n)
        {
            // Drawing more substats than the pool holds is impossible; the generator
            // never does it, but guard so a future pool edit fails loudly.
            throw new ArgumentOutOfRangeException(nameof(k), k,
                $"Cannot draw {k} substats from a pool of {n}.");
        }

        int maxX = Math.Min(d, k);
        var pmf = new double[maxX + 1];

        double denominator = Binomial(n, k);
        for (int x = 0; x <= maxX; x++)
        {
            pmf[x] = (Binomial(d, x) * Binomial(n - d, k - x)) / denominator;
        }

        return pmf;
    }

    /// <summary>
    /// RV distribution for a fresh disc: a mixture over the two possible substat counts.
    /// </summary>
    /// <remarks>
    /// Assuming a fixed <c>k = 3</c> overstates <c>P(RV = 0)</c> by ~33% relative
    /// (0.238 vs the true 0.179) and keeps too many bad discs.
    /// </remarks>
    public static double[] BuildInitialSubstatMixture(int n, int d)
    {
        var three = BuildTable(n, d, 3);
        var four = BuildTable(n, d, 4);

        int maxX = Math.Max(three.Length, four.Length);
        var mixed = new double[maxX];

        for (int x = 0; x < maxX; x++)
        {
            double p = 0;
            if (x < three.Length) p += 0.5 * three[x];
            if (x < four.Length) p += 0.5 * four[x];
            mixed[x] = p;
        }

        return mixed;
    }

    /// <summary>CDF from a PMF, so sampling is one binary search. Last entry forced to 1.0.</summary>
    public static double[] ToCumulative(double[] pmf)
    {
        var cdf = new double[pmf.Length];
        double running = 0;
        for (int i = 0; i < pmf.Length; i++)
        {
            running += pmf[i];
            cdf[i] = running;
        }
        cdf[^1] = 1.0;
        return cdf;
    }

    /// <summary>Inverse-transform sample from a CDF produced by <see cref="ToCumulative"/>.</summary>
    public static int Sample(double[] cdf, double u)
    {
        int lo = 0;
        int hi = cdf.Length - 1;

        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (u < cdf[mid]) hi = mid;
            else lo = mid + 1;
        }

        return lo;
    }

    /// <summary>
    /// RV distribution conditional on a specific main stat.
    /// </summary>
    /// <remarks>
    /// Main stat is drawn first because it changes the pool: a prioritized stat that becomes
    /// the main stat is removed, dropping <c>d</c> by one.
    /// </remarks>
    public static double[] BuildTableFor(int partition, StatType mainStat,
        IReadOnlyCollection<StatType> prioritized)
    {
        return BuildInitialSubstatMixture(
            PoolSizeAfter(mainStat),
            PrioritizedLeftAfter(mainStat, prioritized));
    }

    /// <summary>
    /// RV distribution for a given main stat <i>and</i> substat count — no mixture over k.
    /// </summary>
    /// <remarks>
    /// The fast path draws k before sampling RV precisely to use this. Sampling from the
    /// mixture and drawing k independently leaves the RV marginal correct while corrupting
    /// the composition of low-substat discs.
    /// </remarks>
    public static double[] BuildTableFor(int partition, StatType mainStat, int substatCount,
        IReadOnlyCollection<StatType> prioritized)
    {
        return BuildTable(
            PoolSizeAfter(mainStat),
            PrioritizedLeftAfter(mainStat, prioritized),
            substatCount);
    }

    /// <summary>RV distribution for a partition, mixing over every main stat it can roll.</summary>
    /// <remarks>
    /// Partitions 1-3 have one fixed main stat. 4-6 mix their slot pool, weighted by how often
    /// each main stat occurs — on partition 4 three of six main stats are prioritized, so
    /// ignoring the weights mispredicts the RV tail on the slot players care about most.
    /// </remarks>
    public static double[] BuildPartitionTable(int partition, IReadOnlyCollection<StatType> prioritized)
    {
        var mainStatPool = MainStatPoolFor(partition);

        if (mainStatPool is null)
        {
            var fixedStat = SimulationConfig.MainStatOptionsFor(partition).First();
            return BuildTableFor(partition, fixedStat, prioritized);
        }

        return Mix(mainStatPool
            .Select(mainStat => BuildTableFor(partition, mainStat, prioritized))
            .ToArray());
    }

    /// <summary>
    /// Size of the substat pool after <paramref name="mainStat"/> is excluded.
    /// </summary>
    /// <remarks>
    /// The pool is only shrunk when the main stat actually appears in
    /// <see cref="SimulationConfig.SubstatPool"/>. Slots 5 and 6 offer main stats that are
    /// not substat-pool members at all (PenRatio, AnomalyMastery, Impact, EnergyRegen, and
    /// the five elemental damage stats), so for those the pool keeps its full size. Ignoring
    /// this shifts <c>P(RV = 0)</c> by ~0.035 on slot 5 — enough to visibly over-keep bad discs.
    /// </remarks>
    private static int PoolSizeAfter(StatType mainStat)
    {
        return Array.IndexOf(SimulationConfig.SubstatPool, mainStat) >= 0
            ? SimulationConfig.SubstatPool.Length - 1
            : SimulationConfig.SubstatPool.Length;
    }

    /// <summary>
    /// How many prioritized substats remain available once <paramref name="mainStat"/> is
    /// excluded from the pool. Drops by one only if the main stat is itself prioritized.
    /// </summary>
    private static int PrioritizedLeftAfter(StatType mainStat, IReadOnlyCollection<StatType> prioritized)
    {
        return prioritized.Contains(mainStat) ? prioritized.Count - 1 : prioritized.Count;
    }

    /// <summary>Mixes equal-weight PMF cases into one PMF, trimmed of trailing zeros.</summary>
    private static double[] Mix(double[][] cases)
    {
        int maxLen = cases.Max(c => c.Length);
        var mixed = new double[maxLen];
        double weight = 1.0 / cases.Length;

        foreach (var table in cases)
        {
            for (int x = 0; x < table.Length; x++)
            {
                mixed[x] += weight * table[x];
            }
        }

        int last = mixed.Length - 1;
        while (last > 0 && mixed[last] == 0) last--;

        if (last == mixed.Length - 1) return mixed;

        var trimmed = new double[last + 1];
        Array.Copy(mixed, trimmed, trimmed.Length);
        return trimmed;
    }

    /// <summary>The slot-specific main stat options, or null when the partition has a fixed one.</summary>
    private static StatType[]? MainStatPoolFor(int partition)
    {
        var options = SimulationConfig.MainStatOptionsFor(partition).ToArray();
        // Partitions 1-3 offer exactly one main stat; the fast path never needs the
        // "pool" form for those.
        return options.Length == 1 ? null : options;
    }

    /// <summary>
    /// The prioritized substats that apply to a given partition, matching what
    /// <see cref="SimulationState.GetPrioritizedSubstatsForPartition"/> returns.
    /// </summary>
    /// <remarks>
    /// Used by tests to confirm the sampler's distribution agrees with what real disc
    /// generation produces. Uses the same default set that
    /// <see cref="SimulationState"/> initializes with.
    /// </remarks>
    public static HashSet<StatType> PrioritizedFor(int partition)
    {
        return SimulationState.PrioritizedFor(partition);
    }

    /// <summary>
    /// Binomial coefficient <c>C(n, k)</c> as a <see cref="double"/>. Exact for the
    /// small <c>n</c> permitted by <see cref="MaxExactPoolSize"/>.
    /// </summary>
    private static double Binomial(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        k = Math.Min(k, n - k);

        double result = 1;
        for (int i = 0; i < k; i++)
        {
            result = result * (n - i) / (i + 1);
        }

        return result;
    }
}