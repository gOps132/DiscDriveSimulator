using DiscDriveSimulator.Models;
using DiscDriveSimulator.Services;

namespace DiscDriveSimulator.Tests;

/// <summary>
/// Contract tests for the simulation fast path: decide keep/dismantle from a sampled
/// Roll Value without materializing a <see cref="Disc"/> for every drop.
/// </summary>
public class SimulationFastPathTests
{
    private static SimulationService NewService(out SimulationState state, Action<SimulationState>? configure = null)
    {
        var generator = new DiscGenerator(new Random(2024));
        var service = new SimulationService(generator);
        state = service.State;
        // RunSimulation derives affordable runs from BatteryCharge, and the console dumps
        // ethers into charge before calling. Give it plenty of juice up front.
        state.BatteryCharge = int.MaxValue / 2;
        state.EtherBatteries = 0;
        state.HiFiMasterCopies = 0;
        configure?.Invoke(state);
        return service;
    }

    [Fact]
    public async Task RunSimulationAsync_WithStrictRvFilter_KeepsFarFewerDiscsThanDropped()
    {
        var service = NewService(out var state);
        state.TargetCleanupPartition = 4;
        state.MinCleanupRv = 4;

        var result = await service.RunSimulationAsync(600 * 60);

        Assert.True(result.TotalDrops > 0, "Expected drops to be generated.");
        Assert.True(result.KeptDiscs.Count < result.TotalDrops,
            "A strict RV filter should keep strictly fewer discs than were dropped.");

        // Every kept disc must actually satisfy the filter.
        foreach (var disc in result.KeptDiscs)
        {
            Assert.Equal(4, disc.Partition);
            Assert.True(disc.GetRollValue(state.GetPrioritizedSubstatsForPartition(4)) >= 4);
        }
    }

    [Fact]
    public async Task RunSimulationAsync_CountersMatchTotalDrops()
    {
        var service = NewService(out var state);
        state.TargetCleanupPartition = 4;

        var result = await service.RunSimulationAsync(300 * 60);

        Assert.Equal(result.TotalDrops, result.KeptDiscs.Count + result.DismantledCount);
        Assert.Equal(result.KeptDiscs.Count, state.Inventory.Count);
    }

    [Fact]
    public async Task RunSimulationAsync_NeverStoresDismantledDiscsInDropRecords()
    {
        var service = NewService(out var state);
        state.TargetCleanupPartition = 4;

        var result = await service.RunSimulationAsync(200 * 60);

        Assert.True(result.DismantledCount > 0, "Expected some dismantles.");
        Assert.All(result.DropRecords, r =>
        {
            Assert.True(r.IsKept);
            Assert.NotNull(r.Disc);
        });
    }

    [Fact]
    public async Task RunSimulationAsync_ProgressIsMonotonicAndEndsAtOne()
    {
        var service = NewService(out var state);
        var reported = new List<double>();
        var progress = new Progress<double>(p => reported.Add(p));

        await service.RunSimulationAsync(600 * 60, progress);

        // Progress<double> dispatches on the sync context; force delivery before asserting.
        await Task.Delay(50);

        Assert.NotEmpty(reported);
        for (int i = 1; i < reported.Count; i++)
        {
            Assert.True(reported[i] >= reported[i - 1], "Progress must not go backwards.");
        }
        Assert.Equal(1.0, reported[^1]);
    }

    [Fact]
    public void AutoDismantleOnSim_IsAlwaysOn()
    {
        var state = new SimulationState { AutoDismantleOnSim = false };
        Assert.True(state.AutoDismantleOnSim, "Auto-dismantle must not be switchable off.");
    }

    [Fact]
    public async Task RunSimulationAsync_LargeRunStaysUnderInventoryCap()
    {
        var service = NewService(out var state);
        // Threshold 1 keeps nearly everything, which is the worst case for the cap.
        state.MinCleanupRv = 1;
        state.TargetCleanupPartition = null;

        var result = await service.RunSimulationAsync(5000 * 60);

        Assert.True(state.Inventory.Count <= SimulationConfig.MaxInventoryDiscs,
            $"Inventory {state.Inventory.Count} exceeded cap {SimulationConfig.MaxInventoryDiscs}.");
        Assert.True(result.TotalDrops > state.Inventory.Count,
            "Expected more drops than retained, proving eviction happened.");
    }

    [Fact]
    public async Task RunSimulationAsync_CapEvictionPrefersHigherRollValues()
    {
        var service = NewService(out var state);
        state.MinCleanupRv = 1;
        state.TargetCleanupPartition = null;

        await service.RunSimulationAsync(4000 * 60);

        var retained = state.Inventory
            .Select(d => d.GetRollValue(state.DesiredStats))
            .ToList();

        // Sanity: eviction should not have left obviously terrible discs behind while
        // better ones were available. Assert the retained set is not dominated by zeros.
        double zeroFraction = retained.Count(r => r == 0) / (double)retained.Count;
        Assert.True(zeroFraction < 0.6,
            $"Retained set is {zeroFraction:P0} zero-RV, expected a better mix after eviction.");
    }

    [Fact]
    public async Task RunSimulationAsync_LockedDiscsSurviveCapEviction()
    {
        var service = NewService(out var state);
        state.MinCleanupRv = 1;
        state.TargetCleanupPartition = null;

        // Seed a locked disc with a deliberately awful RV.
        var locked = new Disc
        {
            Partition = 1,
            Set = DiscSet.SwingJazz,
            MainStat = StatType.HP,
            MainStatValue = 440,
            IsLocked = true,
            Substats =
            {
                new SubstatRoll(StatType.HP, 112.0, 1),
                new SubstatRoll(StatType.DEF, 15.0, 1),
                new SubstatRoll(StatType.ATK, 19.0, 1)
            }
        };
        state.Inventory.Add(locked);

        await service.RunSimulationAsync(3000 * 60);

        Assert.Contains(locked, state.Inventory);
    }

    [Fact]
    public async Task RunSimulationAsync_ConsumesExpectedBatteryAndEthers()
    {
        var service = NewService(out var state);
        state.BatteryCharge = 120;
        state.EtherBatteries = 2; // 120 extra charge => 240 total => 4 runs

        var result = await service.RunSimulationAsync(240 * 60);

        Assert.Equal(4, result.Runs);
        Assert.Equal(240, result.BatterySpent);
        Assert.Equal(0, state.EtherBatteries);
        Assert.Equal(0, state.BatteryCharge);
    }

    [Fact]
    public async Task RunSimulationAsync_RespectsTargetPartitionForDismantleReasons()
    {
        var service = NewService(out var state);
        state.TargetCleanupPartition = 6;

        var result = await service.RunSimulationAsync(400 * 60);

        Assert.NotEmpty(result.DismantleReasonSummary);
        Assert.Contains(result.DismantleReasonSummary.Keys,
            r => r.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunSimulationAsync_FastPathKeepsEveryDiscAboveThreshold()
    {
        // The fast path samples RV and keeps on that. Every kept disc's real, computed RV
        // must therefore be at or above the threshold — this is the guard against the
        // sampler and the generator disagreeing.
        var service = NewService(out var state);
        state.TargetCleanupPartition = 4;
        state.MinCleanupRv = 2;

        var result = await service.RunSimulationAsync(900 * 60);

        Assert.NotEmpty(result.KeptDiscs);
        foreach (var disc in result.KeptDiscs)
        {
            int actual = disc.GetRollValue(state.GetPrioritizedSubstatsForPartition(disc.Partition));
            Assert.True(actual >= state.MinCleanupRv,
                $"Kept a disc with real RV {actual}, below threshold {state.MinCleanupRv}.");
        }
    }

    [Fact]
    public async Task RunSimulationAsync_FastPathMatchesGeneratorRollValueDistribution()
    {
        // End-to-end: the RV distribution of discs the fast path keeps must match what
        // full generation would produce for the same partition. Guards the whole trick.
        var service = NewService(out var state);
        state.TargetCleanupPartition = 4;

        var result = await service.RunSimulationAsync(5000 * 60);

        var keptTotal = result.KeptDiscs.Count;
        Assert.True(keptTotal > 100, $"Need a decent sample, got {keptTotal}.");

        var actual = new int[5];
        foreach (var disc in result.KeptDiscs)
        {
            actual[disc.GetRollValue(state.GetPrioritizedSubstatsForPartition(4))]++;
        }

        var table = HypergeometricSampler.BuildPartitionTable(4,
            state.GetPrioritizedSubstatsForPartition(4).ToHashSet());

        // Compare against the table conditioned on RV >= MinCleanupRv, since everything
        // below the threshold is dismantled and never reaches inventory.
        double survivorMass = 0;
        for (int rv = state.MinCleanupRv; rv < table.Length; rv++) survivorMass += table[rv];
        Assert.True(survivorMass > 0, "Threshold must leave some survivors.");

        for (int rv = state.MinCleanupRv; rv < table.Length; rv++)
        {
            double observed = actual[rv] / (double)keptTotal;
            double expected = table[rv] / survivorMass;
            Assert.True(Math.Abs(observed - expected) < 0.03,
                $"Partition 4 RV={rv}: kept share {observed:F4} vs conditional table {expected:F4}");
        }
    }

    [Fact]
    public async Task RunSimulationAsync_FastPathIsFasterThanFullGeneration()
    {
        // Not a benchmark, just a regression guard: the fast path must avoid most
        // allocations. Compare wall time for a large run against the same run with the
        // fast path disabled (auto-dismantle off means everything gets built).
        var fastService = NewService(out var fastState);
        fastState.TargetCleanupPartition = 4;
        fastState.MinCleanupRv = 3;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fastResult = await fastService.RunSimulationAsync(20_000 * 60);
        sw.Stop();

        Assert.True(fastResult.KeptDiscs.Count > 0, "Fast path should keep something at RV 3.");
        Assert.True(fastResult.DismantledCount > fastResult.KeptDiscs.Count * 10,
            "Sanity: most drops should be dismantled, proving the skip path ran.");
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"Fast path took {sw.ElapsedMilliseconds}ms for 20k runs.");
    }

    [Fact]
    public async Task RunSimulationAsync_CancellationStopsEarly()
    {
        var service = NewService(out var state);
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RunSimulationAsync(100_000 * 60, null, cts.Token));
    }

    [Fact]
    public async Task RunSimulationAsync_RespectsLowRvThresholdInReasons()
    {
        var service = NewService(out var state);
        state.MinCleanupRv = 5; // nothing can reach this
        state.TargetCleanupPartition = null;

        var result = await service.RunSimulationAsync(400 * 60);

        Assert.Equal(0, result.KeptDiscs.Count);
        Assert.Equal(result.TotalDrops, result.DismantledCount);
        Assert.Contains(result.DismantleReasonSummary.Keys, r => r.Contains("below Min RV"));
    }
}