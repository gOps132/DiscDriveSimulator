using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

// Closed-form sampler for Roll Value, letting the fast path decide keep/dismantle without
// materializing a Disc.
//
// RV is the count of drawn substats that are prioritized: every roll has exactly one hit and
// nothing increments it. Substats are drawn as a shuffle-and-take-prefix, which draws
// *distinct* pool elements — sampling without replacement. So with pool size n after main-stat
// exclusion, d prioritized stats surviving in it, and k drawn:
//
//   X ~ Hypergeometric(n, d, k),  i.e.  P(X = x) = C(d,x)·C(n-d,k-x) / C(n,k)
//
// The distribution depends only on the counts (n, d, k), so it collapses to precomputed
// tables and one binary search replaces the whole shuffle-and-count pipeline. A dismantled
// drop then costs one uniform double.
//
// Traps. Two subtleties are load-bearing, and both were bugs first: the substat count is
// itself random (3 or 4), so RV is a *mixture* over k; and a main stat only shrinks the
// pool if it is a pool member, which slots 5 and 6 offer several that are not.
//
// Tables are ratios of binomial coefficients in double, exact at n <= 10, k <= 4. The guards
// throw rather than degrade: a silently-wrong distribution is worse than a crash. Full
// derivation in docs/hypergeometric-sampling.md.
public static class HypergeometricSampler
{
    // Largest pool size for which the closed form stays exact in double.
    public const int MaxExactPoolSize = 20;

    // Largest draw count for which the closed form stays exact in double.
    public const int MaxExactDrawCount = 8;

    // PMF for X ~ Hypergeometric(n, d, k):
    //   n: pool size after excluding the disc's main stat
    //   d: prioritized stats surviving in that pool
    //   k: substats drawn (3 or 4 in this game)
    //
    // Indexed by x, trimmed to min(d, k) + 1 entries since RV cannot exceed min(d, k).
    //
    // Throws if n exceeds MaxExactPoolSize, k exceeds MaxExactDrawCount, k < 1, or d > n.
    // Those bounds keep the binomial ratios exact in double; widening the pool or the substat
    // count would need a log-gamma based implementation instead.
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

    // RV distribution for a fresh disc: a mixture over the two possible substat counts.
    // Assuming a fixed k = 3 overstates P(RV = 0) by ~33% relative
    // (0.238 vs the true 0.179) and keeps too many bad discs.
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

    // CDF from a PMF, so sampling is one binary search. Last entry forced to 1.0.
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

    // Inverse-transform sample from a CDF produced by ToCumulative.
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

    // RV distribution conditional on a specific main stat.
    // Main stat is drawn first because it changes the pool: a prioritized stat that becomes
    // the main stat is removed, dropping d by one.
    public static double[] BuildTableFor(int partition, StatType mainStat,
        IReadOnlyCollection<StatType> prioritized)
    {
        return BuildInitialSubstatMixture(
            PoolSizeAfter(mainStat),
            PrioritizedLeftAfter(mainStat, prioritized));
    }

    // RV distribution for a given main stat and substat count — no mixture over k.
    // The fast path draws k before sampling RV precisely to use this. Sampling from the
    // mixture and drawing k independently leaves the RV marginal correct while corrupting
    // the composition of low-substat discs.
    public static double[] BuildTableFor(int partition, StatType mainStat, int substatCount,
        IReadOnlyCollection<StatType> prioritized)
    {
        return BuildTable(
            PoolSizeAfter(mainStat),
            PrioritizedLeftAfter(mainStat, prioritized),
            substatCount);
    }

    // RV distribution for a partition, mixing over every main stat it can roll.
    // Partitions 1-3 have one fixed main stat. 4-6 mix their slot pool, weighted by how often
    // each main stat occurs — on partition 4 three of six main stats are prioritized, so
    // ignoring the weights mispredicts the RV tail on the slot players care about most.
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

    // Size of the substat pool after mainStat is excluded.
    // The pool is only shrunk when the main stat actually appears in
    // SimulationConfig.SubstatPool. Slots 5 and 6 offer main stats that are
    // not substat-pool members at all (PenRatio, AnomalyMastery, Impact, EnergyRegen, and
    // the five elemental damage stats), so for those the pool keeps its full size. Ignoring
    // this shifts P(RV = 0) by ~0.035 on slot 5 — enough to visibly over-keep bad discs.
    private static int PoolSizeAfter(StatType mainStat)
    {
        return Array.IndexOf(SimulationConfig.SubstatPool, mainStat) >= 0
            ? SimulationConfig.SubstatPool.Length - 1
            : SimulationConfig.SubstatPool.Length;
    }

    // How many prioritized substats remain available once mainStat is
    // excluded from the pool. Drops by one only if the main stat is itself prioritized.
    private static int PrioritizedLeftAfter(StatType mainStat, IReadOnlyCollection<StatType> prioritized)
    {
        return prioritized.Contains(mainStat) ? prioritized.Count - 1 : prioritized.Count;
    }

    // Mixes equal-weight PMF cases into one PMF, trimmed of trailing zeros.
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

    // The slot-specific main stat options, or null when the partition has a fixed one.
    private static StatType[]? MainStatPoolFor(int partition)
    {
        var options = SimulationConfig.MainStatOptionsFor(partition).ToArray();
        // Partitions 1-3 offer exactly one main stat; the fast path never needs the
        // "pool" form for those.
        return options.Length == 1 ? null : options;
    }

    // The prioritized substats that apply to a given partition, matching what
    // SimulationState.GetPrioritizedSubstatsForPartition returns.
    // Used by tests to confirm the sampler's distribution agrees with what real disc
    // generation produces. Uses the same default set that
    // SimulationState initializes with.
    public static HashSet<StatType> PrioritizedFor(int partition)
    {
        return SimulationState.PrioritizedFor(partition);
    }

    // Binomial coefficient C(n, k) as a double. Exact for the
    // small n permitted by MaxExactPoolSize.
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