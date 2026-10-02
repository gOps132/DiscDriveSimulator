using DiscDriveSimulator.Models;
using DiscDriveSimulator.Services;

namespace DiscDriveSimulator.Tests;

/// <summary>
/// Test-local subset enumerator. Kept as a plain static class (not an extension in a
/// nested scope) so the recursive combination generator compiles.
/// </summary>
internal static class SubsetExtensions
{
    internal static IEnumerable<int[]> Combinations(this int[] source, int k)
    {
        if (k == 0)
        {
            yield return Array.Empty<int>();
            yield break;
        }

        int[] indices = Enumerable.Range(0, source.Length).ToArray();
        foreach (int[] combo in Combinations(0, 0, k, indices, source))
        {
            yield return combo;
        }
    }

    private static IEnumerable<int[]> Combinations(int start, int depth, int k,
        int[] indices, int[] source)
    {
        if (depth == k)
        {
            var result = new int[k];
            for (int i = 0; i < k; i++) result[i] = source[indices[i]];
            yield return result;
            yield break;
        }

        for (int i = start; i < source.Length; i++)
        {
            indices[depth] = i;
            foreach (var c in Combinations(i + 1, depth + 1, k, indices, source))
            {
                yield return c;
            }
        }
    }
}

/// <summary>
/// Verifies the closed-form hypergeometric sampler used by the simulation fast path.
///
/// The core claim under test: RV (roll value) is the number of drawn substats that
/// fall inside the partition's prioritized set, and drawing k distinct substats from
/// a pool without replacement makes RV exactly hypergeometric. If that equivalence
/// holds, the fast path can decide keep/dismantle without materializing a Disc.
/// </summary>
public class HypergeometricSamplerTests
{
    // ---- Exactness of the closed form -------------------------------------------------

    [Fact]
    public void Pmf_SumsToOne_ForAllValidParameters()
    {
        for (int n = 2; n <= 10; n++)
        {
            for (int d = 0; d <= n; d++)
            {
                for (int k = 1; k <= Math.Min(n, HypergeometricSampler.MaxExactDrawCount); k++)
                {
                    var table = HypergeometricSampler.BuildTable(n, d, k);
                    double total = 0;
                    for (int x = 0; x < table.Length; x++)
                    {
                        total += table[x];
                    }

                    Assert.True(Math.Abs(total - 1.0) < 1e-12,
                        $"PMF must sum to 1. Got {total} for n={n} d={d} k={k}");
                }
            }
        }
    }

    [Fact]
    public void Pmf_MatchesBruteForceCounting()
    {
        // Exhaustive count over every k-subset of an n-element pool, cross-checked
        // against the closed form. This is the ground truth for the formula.
        for (int n = 2; n <= 9; n++)
        {
            for (int d = 0; d <= n; d++)
            {
                for (int k = 1; k <= Math.Min(n, HypergeometricSampler.MaxExactDrawCount); k++)
                {
                    var table = HypergeometricSampler.BuildTable(n, d, k);
                    int maxX = Math.Min(d, k);
                    int[] brute = new int[maxX + 1];

                    foreach (var subset in EnumerateSubsets(n, k))
                    {
                        int hits = subset.Count(i => i < d);
                        brute[hits]++;
                    }

                    int totalSubsets = brute.Sum();
                    for (int x = 0; x <= maxX; x++)
                    {
                        double expected = (double)brute[x] / totalSubsets;
                        Assert.True(Math.Abs(table[x] - expected) < 1e-12,
                            $"P(X={x}) mismatch for n={n} d={d} k={k}: " +
                            $"closed-form {table[x]} vs brute {expected}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Table_HasOneEntryPerPossibleOutcomePlusOne()
    {
        // RV can never exceed min(D, k), so trailing zeros are dropped. Anything
        // beyond k+1 entries would be dead weight in the hot loop.
        // Length is min(d, k) + 1: RV cannot exceed either the desired count or the draw count.
        Assert.Equal(4, HypergeometricSampler.BuildTable(10, 3, 4).Length);
        Assert.Equal(2, HypergeometricSampler.BuildTable(10, 1, 4).Length);
        Assert.Equal(5, HypergeometricSampler.BuildTable(10, 10, 4).Length);
        Assert.Equal(5, HypergeometricSampler.BuildTable(10, 4, 4).Length);
        Assert.Equal(4, HypergeometricSampler.BuildTable(10, 3, 3).Length);
    }

    [Fact]
    public void BuildTable_RejectsParametersOutsideExactDoubleRange()
    {
        // Guard from Q4: fail loudly rather than silently returning wrong numbers.
        Assert.Throws<ArgumentOutOfRangeException>(() => HypergeometricSampler.BuildTable(25, 3, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => HypergeometricSampler.BuildTable(10, 3, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => HypergeometricSampler.BuildTable(10, 11, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => HypergeometricSampler.BuildTable(10, 3, 0));
    }

    [Fact]
    public void ToCumulative_IsNonDecreasingAndEndsAtExactlyOne()
    {
        var pmf = HypergeometricSampler.BuildTable(10, 3, 4);
        var cdf = HypergeometricSampler.ToCumulative(pmf);

        for (int i = 1; i < cdf.Length; i++)
        {
            Assert.True(cdf[i] >= cdf[i - 1], "CDF must be non-decreasing.");
        }

        // Exactly 1.0, not approximately: a uniform draw of 1.0 must not fall off the end.
        Assert.Equal(1.0, cdf[^1]);
    }

    // ---- Empirical fidelity -----------------------------------------------------------

    [Fact]
    public void Sample_EmpiricalFrequenciesMatchThePmf()
    {
        const int trials = 400_000;
        var rng = new Random(12345);
        var pmf = HypergeometricSampler.BuildTable(10, 3, 4);
        var cdf = HypergeometricSampler.ToCumulative(pmf);
        var counts = new int[pmf.Length];

        for (int i = 0; i < trials; i++)
        {
            counts[HypergeometricSampler.Sample(cdf, rng.NextDouble())]++;
        }

        for (int x = 0; x < pmf.Length; x++)
        {
            double empirical = (double)counts[x] / trials;
            Assert.True(Math.Abs(empirical - pmf[x]) < 0.005,
                $"X={x}: empirical {empirical:F5} vs expected {pmf[x]:F5} (tolerance 0.005)");
        }
    }

    [Fact]
    public void Sample_StaysInRangeForExtremeUniforms()
    {
        var pmf = HypergeometricSampler.BuildTable(10, 3, 4);
        var cdf = HypergeometricSampler.ToCumulative(pmf);
        int max = pmf.Length - 1;

        Assert.Equal(0, HypergeometricSampler.Sample(cdf, 0.0));
        Assert.Equal(0, HypergeometricSampler.Sample(cdf, double.Epsilon));
        Assert.Equal(max, HypergeometricSampler.Sample(cdf, 0.9999999));
        Assert.Equal(max, HypergeometricSampler.Sample(cdf, 1.0 - 1e-15));
    }

    [Fact]
    public void ToCumulative_PreservesEveryPmfEntryExactly()
    {
        // The CDF is a transformation, not a filter: no probability mass may be lost,
        // or the simulation silently under- or over-counts high-RV discs.
        for (int n = 2; n <= 10; n++)
        {
            for (int d = 0; d <= n; d++)
            {
                for (int k = 1; k <= Math.Min(n, HypergeometricSampler.MaxExactDrawCount); k++)
                {
                    var pmf = HypergeometricSampler.BuildTable(n, d, k);
                    var cdf = HypergeometricSampler.ToCumulative(pmf);

                    for (int x = 1; x < pmf.Length; x++)
                    {
                        double recovered = cdf[x] - cdf[x - 1];
                        Assert.True(Math.Abs(recovered - pmf[x]) < 1e-12,
                            $"Mass lost converting to CDF for n={n} d={d} k={k} x={x}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Sample_AgreesWithRealDiscGenerationOverManyRolls()
    {
        // The integration guarantee: sampling RV directly must match the RV that
        // the real generator actually produces, distributionally.
        var rng = new Random(999);
        var generator = new DiscGenerator(rng);

        // Partition 1 has a fixed HP main stat, which is not in the prioritized set,
        // so the pool is SubstatPool minus HP => n=9, d=3.
        var expected = HypergeometricSampler.BuildPartitionTable(1, HypergeometricSampler.PrioritizedFor(1));
        var counts = new int[expected.Length];

        const int trials = 120_000;
        for (int i = 0; i < trials; i++)
        {
            var disc = generator.GenerateDisc(partition: 1, set: DiscSet.SwingJazz);
            int rv = disc.GetRollValue(HypergeometricSampler.PrioritizedFor(1));
            Assert.InRange(rv, 0, expected.Length - 1);
            counts[rv]++;
        }

        for (int x = 0; x < expected.Length; x++)
        {
            double empirical = (double)counts[x] / trials;
            Assert.True(Math.Abs(empirical - expected[x]) < 0.01,
                $"RV={x}: generated {empirical:F4} vs hypergeometric mixture {expected[x]:F4}");
        }
    }

    [Fact]
    public void PartitionTable_MatchesRealGenerationForEveryPartition()
    {
        // The full integration guarantee across all six slots, including 4-6 where the
        // main stat is itself drawn and may be prioritized (lowering d for that case).
        var rng = new Random(4242);
        var generator = new DiscGenerator(rng);

        foreach (int partition in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var prioritized = HypergeometricSampler.PrioritizedFor(partition);
            var expected = HypergeometricSampler.BuildPartitionTable(partition, prioritized);
            var counts = new int[expected.Length];

            const int trials = 120_000;
            for (int i = 0; i < trials; i++)
            {
                var disc = generator.GenerateDisc(partition: partition, set: DiscSet.SwingJazz);
                int rv = disc.GetRollValue(prioritized);
                Assert.InRange(rv, 0, expected.Length - 1);
                counts[rv]++;
            }

            for (int x = 0; x < expected.Length; x++)
            {
                double empirical = (double)counts[x] / trials;
                Assert.True(Math.Abs(empirical - expected[x]) < 0.01,
                    $"Partition {partition} RV={x}: generated {empirical:F4} vs {expected[x]:F4}");
            }
        }
    }

    [Fact]
    public void PartitionTable_SumsToOneForEveryPartition()
    {
        foreach (int partition in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var table = HypergeometricSampler.BuildPartitionTable(
                partition, HypergeometricSampler.PrioritizedFor(partition));

            double total = 0;
            foreach (double p in table) total += p;

            Assert.True(Math.Abs(total - 1.0) < 1e-12,
                $"Partition {partition} table must sum to 1, got {total}");
        }
    }

    // ---- Helpers ----------------------------------------------------------------------

    private static IEnumerable<int[]> EnumerateSubsets(int n, int k)
    {
        var indices = Enumerable.Range(0, n).ToArray();
        return indices.Combinations(k);
    }
}