using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

/// <summary>
/// Closed-form sampler for Roll Value (RV), used by the simulation fast path to decide
/// keep/dismantle <em>without</em> materializing a <see cref="Disc"/>.
/// </summary>
/// <remarks>
/// <para><b>Why RV is hypergeometric.</b> RV is the sum of <c>SubstatRoll.RollHits</c>
/// over the substats that appear in a partition's prioritized set
/// (see <see cref="Disc.GetRollValue(HashSet{StatType}?)"/>). Every substat roll is
/// created with exactly one hit — <see cref="DiscGenerator"/> never increments it — so
/// RV collapses to <i>the count of drawn substats that are prioritized</i>.
/// </para>
///
/// <para><b>Why the draws are without replacement.</b> <see cref="DiscGenerator.GenerateDisc"/>
/// builds its pool as <c>SubstatPool</c> minus the disc's main stat, Fisher-Yates shuffles
/// it, and takes the first <c>k ∈ {3, 4}</c> entries. A shuffle-and-take-prefix draws
/// <c>k</c> <i>distinct</i> pool elements, each with equal probability — the textbook
/// "sampling without replacement" model.
/// </para>
///
/// <para>Consequently, with pool size <c>n</c> (after main-stat exclusion), <c>d</c>
/// prioritized stats still present in the pool, and <c>k</c> substats drawn:</para>
///
/// <code>
///              d      n - d
///         C( , x) · C(     , k - x)      d    n - d      C(n, k)
// P(X = x) = -------------------------  =  --------  · -----------------
///              n      n                 C(n, x)  C(n, k)      C(n, k)
/// </code>
///
/// <para>That is the hypergeometric distribution: <c>X ~ Hypergeometric(n, d, k)</c>.
/// It depends only on the <i>counts</i> <c>(n, d, k)</c> — never on which specific stats
/// the priority list names, nor on which set was rolled. So the whole distribution
/// collapses to a handful of precomputed tables, and one binary search over a CDF
/// replaces the entire substat-shuffle-and-count pipeline.
/// </para>
///
/// <para><b>Consequences for the simulation.</b> Because RV is known before any substats
/// are built, a disc that will be dismantled costs a single uniform double plus a
/// binary search — no <see cref="Disc"/>, no <see cref="Guid"/>, no LINQ, no
/// <c>SubstatRoll</c> allocations. Only kept discs are materialized. See
/// <c>docs/hypergeometric-sampling.md</c> for the full derivation and worked example.</para>
///
/// <para><b>Numerical exactness.</b> Tables are built as ratios of binomial coefficients
/// evaluated in <see cref="double"/>. That is exact for the sizes this game actually uses
/// (<c>n ≤ 10</c>, <c>k ≤ 4</c>), where <c>C(10, 4) = 210</c> is far inside double
/// precision. The guard constants below throw rather than degrade silently, because a
/// silently-wrong distribution in a simulator is worse than a crash.
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
    /// Builds the RV distribution for a freshly generated disc, which is a
    /// <em>mixture</em> over substat counts rather than a single hypergeometric.
    /// </summary>
    /// <remarks>
    /// <see cref="DiscGenerator.GenerateDisc"/> does
    /// <c>int initialSubCount = _random.Next(2) == 0 ? 3 : 4;</c> — the disc's substat
    /// count is itself random, chosen uniformly. So the true RV distribution is
    /// <code>0.5 · Hypergeom(n, d, 3) + 0.5 · Hypergeom(n, d, 4)</code>, and using
    /// either term alone skews the tail. Measured against real generation, assuming a
    /// fixed <c>k = 3</c> overstates <c>P(RV = 0)</c> by roughly 33% relative
    /// (0.238 vs the true 0.179), which would keep noticeably too many bad discs.
    /// </remarks>
    /// <param name="n">Pool size after excluding the disc's main stat.</param>
    /// <param name="d">Number of prioritized stats present in that pool.</param>
    /// <returns>A mixed PMF indexed by RV, summing to exactly 1.</returns>
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

    /// <summary>
    /// Builds a cumulative distribution from a PMF so sampling is a single binary search.
    /// The last entry is forced to exactly 1.0 so cumulative rounding can never leave
    /// a uniform draw of 1.0 falling off the end.
    /// </summary>
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

    /// <summary>
    /// Samples <c>X ~ Hypergeometric(n, d, k)</c> from a precomputed CDF using inverse
    /// transform sampling.
    /// </summary>
    /// <param name="cdf">
    /// A CDF from <see cref="ToCumulative"/> for the <c>(n, d, k)</c> of interest.
    /// </param>
    /// <param name="u">A uniform draw in <c>[0, 1)</c> — use <c>Random.NextDouble()</c>.</param>
    /// <returns>The sampled value of <c>X</c>, in <c>[0, cdf.Length - 1]</c>.</returns>
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
    /// Builds the RV distribution for a disc with a <i>specific</i> main stat.
    /// </summary>
    /// <remarks>
    /// Used by the simulation fast path, which draws the main stat first and then needs the
    /// RV distribution <em>conditional</em> on it. The main stat must be drawn before RV
    /// because it changes the pool: a prioritized stat that becomes the main stat is removed
    /// from the substat pool, dropping <c>d</c> by one.
    /// </remarks>
    /// <param name="partition">Partition 1-6.</param>
    /// <param name="mainStat">The disc's main stat.</param>
    /// <param name="prioritized">Prioritized substats for that partition.</param>
    public static double[] BuildTableFor(int partition, StatType mainStat,
        IReadOnlyCollection<StatType> prioritized)
    {
        return BuildInitialSubstatMixture(
            PoolSizeAfter(mainStat),
            PrioritizedLeftAfter(mainStat, prioritized));
    }

    /// <summary>
    /// Builds the RV distribution for a disc with a given main stat <i>and</i> a given
    /// substat count — the conditional case, with no mixture over <c>k</c>.
    /// </summary>
    /// <remarks>
    /// The fast path draws <c>k</c> before sampling RV precisely so it can use this. Sampling
    /// RV from the mixture and then drawing <c>k</c> independently leaves the RV marginal
    /// correct while corrupting the composition of discs with fewer substats.
    /// </remarks>
    public static double[] BuildTableFor(int partition, StatType mainStat, int substatCount,
        IReadOnlyCollection<StatType> prioritized)
    {
        return BuildTable(
            PoolSizeAfter(mainStat),
            PrioritizedLeftAfter(mainStat, prioritized),
            substatCount);
    }

    /// <summary>
    /// Builds the full RV distribution for a disc on a given partition, mixing over every
    /// possible main stat.
    /// </summary>
    /// <remarks>
    /// There are <b>two</b> independent sources, and missing either skews the result:
    /// <list type="number">
    /// <item><b>Substat count.</b> <see cref="DiscGenerator.GenerateDisc"/> picks
    /// 3 or 4 substats uniformly, so each main-stat case is itself a mixture over
    /// <c>k ∈ {3, 4}</c> — see <see cref="BuildInitialSubstatMixture"/>.</item>
    /// <item><b>Main stat.</b> Partitions 1-3 have a fixed main stat (HP/ATK/DEF), giving a
    /// single case. Partitions 4-6 pick uniformly from a slot-specific pool, and when a
    /// prioritized stat happens to <i>be</i> the main stat it is removed from the pool,
    /// dropping <c>d</c> by one. That case must be weighted by how often the main stat is
    /// that stat.</item>
    /// </list>
    /// For the default priorities (CritRate, CritDmg, ATKPercent) this matters a lot on
    /// partition 4: CritRate/CritDmg/ATKPercent are 3 of its 6 possible main stats, so
    /// ignoring the mix mispredicts the RV tail on the slot people care about most.
    /// </remarks>
    /// <param name="partition">Partition 1-6.</param>
    /// <param name="prioritized">Prioritized substats for that partition.</param>
    /// <returns>A PMF indexed by RV, summing to exactly 1.</returns>
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