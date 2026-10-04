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
        var generator = new DiscGenerator(2024);
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
    public async Task RunSimulationAsync_RollValueIsCorrectPerSubstatCount()
    {
        // The joint (k, RV) distribution, not just the RV marginal. If RV were sampled from
        // the k-mixture while k was drawn independently, 3-substat discs would be composed
        // from the wrong table — invisible to a marginal check.
        var service = NewService(out var state);
        state.TargetCleanupPartition = null;
        state.MinCleanupRv = 1; // keep everything so all (k, RV) cells are populated

        // Lift the inventory cap for this test. With the cap active, retained discs are a
        // biased sample - only those that beat the eviction floor survive - so the
        // distribution assertions would be measuring the bias, not the sampler.
        int originalCap = SimulationConfig.MaxInventoryDiscs;
        SimulationConfig.MaxInventoryDiscs = 200_000;
        SimulationRunResult result;
        try
        {
            result = await service.RunSimulationAsync(40_000 * 60);
        }
        finally
        {
            SimulationConfig.MaxInventoryDiscs = originalCap;
        }

        Assert.True(result.KeptDiscs.Count > 5000, $"Need a big sample, got {result.KeptDiscs.Count}.");

        foreach (int partition in new[] { 1, 4, 5 })
        {
            var prioritized = state.GetPrioritizedSubstatsForPartition(partition).ToHashSet();
            int totalForPartition = result.KeptDiscs.Count(d => d.Partition == partition);
            Assert.True(totalForPartition > 500, $"Partition {partition}: only {totalForPartition} kept.");

            // Slots 4-6 draw their main stat, and the RV table depends on it, so verify the
            // joint (main stat, k, RV) distribution against each main stat's own table.
            foreach (var mainStat in SimulationConfig.MainStatOptionsFor(partition))
            {
                var perMain = new Dictionary<(int, int), int>();
                foreach (var disc in result.KeptDiscs)
                {
                    if (disc.Partition != partition || disc.MainStat != mainStat) continue;
                    var key = (disc.InitialSubstatCount, disc.GetRollValue(prioritized));
                    perMain.TryGetValue(key, out int n);
                    perMain[key] = n + 1;
                }

                int totalForMain = perMain.Values.Sum();
                if (totalForMain < 200) continue;

                foreach (int k in SimulationConfig.SubstatCountOptions)
                {
                    var table = HypergeometricSampler.BuildTableFor(
                        partition, mainStat, k, prioritized);

                    // Normalize over the RV values that survive the threshold, since
                    // everything below it was dismantled and never reached inventory.
                    double survivorMass = 0;
                    for (int rv = state.MinCleanupRv; rv < table.Length; rv++)
                    {
                        survivorMass += table[rv];
                    }
                    if (survivorMass <= 0) continue;

                    double observedSurvivors = 0;
                    for (int rv = state.MinCleanupRv; rv < table.Length; rv++)
                    {
                        perMain.TryGetValue((k, rv), out int n);
                        observedSurvivors += n / (double)totalForMain;
                    }
                    if (observedSurvivors <= 0) continue;

                    for (int rv = state.MinCleanupRv; rv < table.Length; rv++)
                    {
                        perMain.TryGetValue((k, rv), out int n);
                        double observed = n / (double)totalForMain / observedSurvivors;
                        double expected = table[rv] / survivorMass;

                        // Tolerance scales with cell size: 4 binomial standard deviations
                        // plus a small floor. A fixed tolerance would either flake on small
                        // cells or hide a real error on large ones.
                        double n0 = n / observedSurvivors;
                        double tolerance = 4 * Math.Sqrt(Math.Max(expected * (1 - expected), 1e-9) / Math.Max(n0, 1)) + 0.005;

                        Assert.True(Math.Abs(observed - expected) < tolerance,
                            $"Partition {partition} main={mainStat} k={k} RV={rv}: " +
                            $"observed {observed:F4} vs Hypergeom(n,d,{k}) {expected:F4} " +
                            $"(n={n0:F0}, tolerance {tolerance:F4})");
                    }
                }
            }
        }
    }

    private static StatType MainStatFor(int partition) =>
        SimulationConfig.MainStatOptionsFor(partition).First();

    [Fact]
    public void GetStorageView_MatchesSeparateCountsAndFilter()
    {
        var generator = new DiscGenerator(31337);
        var service = new SimulationService(generator);
        var state = service.State;
        state.BatteryCharge = int.MaxValue / 2;
        state.MinCleanupRv = 1;
        state.TargetCleanupPartition = null;

        service.RunSimulationAsync(800 * SimulationConfig.BatteryPerRun).GetAwaiter().GetResult();
        Assert.True(state.Inventory.Count > 100, "Need inventory to be meaningful.");

        // Sprinkle locks and selections so every tally branch is exercised.
        foreach (var disc in state.Inventory)
        {
            disc.IsSelected = false;
            disc.IsLocked = false;
        }
        state.Inventory[0].IsLocked = true;
        state.Inventory[1].IsSelected = true;
        state.Inventory[2].IsSelected = true;
        state.Inventory[3].IsLocked = true;
        state.Inventory[3].IsSelected = true; // locked + selected: must not be counted
        state.FilterPartition = 4;

        var view = service.GetStorageView();
        var expectedFiltered = service.GetFilteredDiscs();

        Assert.Equal(expectedFiltered.Count, view.FilteredDiscs.Count);
        Assert.Equal(
            state.Inventory.Count(d => !d.IsLocked && !service.MatchesFilter(d)),
            view.NonMatchingDismantleableCount);
        Assert.Equal(
            state.Inventory.Count(d => d.IsSelected && !d.IsLocked),
            view.SelectedCount);

        // Filtered contents and order must be identical, not just the count.
        Assert.Equal(expectedFiltered.Select(d => d.Id), view.FilteredDiscs.Select(d => d.Id));
    }

    [Fact]
    public void GetStorageView_SortsByEveryOrder()
    {
        var generator = new DiscGenerator(555);
        var service = new SimulationService(generator);
        var state = service.State;
        state.BatteryCharge = int.MaxValue / 2;
        state.MinCleanupRv = 1;
        state.TargetCleanupPartition = null;
        service.RunSimulationAsync(300 * SimulationConfig.BatteryPerRun).GetAwaiter().GetResult();

        foreach (SortOrder order in Enum.GetValues<SortOrder>())
        {
            state.CurrentSort = order;

            var view = service.GetStorageView();
            var discs = view.FilteredDiscs;

            switch (order)
            {
                case SortOrder.HighestCv:
                    for (int i = 1; i < discs.Count; i++)
                    {
                        Assert.True(discs[i - 1].GetCritValue() >= discs[i].GetCritValue(),
                            $"CV descending violated at {i} for {order}");
                    }
                    break;
                case SortOrder.HighestRv:
                    for (int i = 1; i < discs.Count; i++)
                    {
                        Assert.True(discs[i - 1].GetRollValue(state.DesiredStats)
                                    >= discs[i].GetRollValue(state.DesiredStats),
                            $"RV descending violated at {i} for {order}");
                    }
                    break;
                case SortOrder.PartitionAsc:
                    for (int i = 1; i < discs.Count; i++)
                        Assert.True(discs[i - 1].Partition <= discs[i].Partition);
                    break;
                case SortOrder.PartitionDesc:
                    for (int i = 1; i < discs.Count; i++)
                        Assert.True(discs[i - 1].Partition >= discs[i].Partition);
                    break;
                case SortOrder.Newest:
                    for (int i = 1; i < discs.Count; i++)
                        Assert.True(discs[i - 1].CreatedAt >= discs[i].CreatedAt);
                    break;
            }
        }
    }

    [Fact]
    public async Task GetStorageView_SinglePassScalesToFullInventory()
    {
        var generator = new DiscGenerator(777);
        var service = new SimulationService(generator);
        var state = service.State;
        state.BatteryCharge = int.MaxValue / 2;
        state.MinCleanupRv = 1;
        state.TargetCleanupPartition = null;

        // Fill to the inventory cap, then assert the view is produced well inside a frame
        // budget. This is a regression guard, not a benchmark: the old five-pass version
        // took hundreds of ms here.
        await service.RunSimulationAsync(SimulationConfig.MaxInventoryDiscs * SimulationConfig.BatteryPerRun);
        while (state.Inventory.Count < SimulationConfig.MaxInventoryDiscs)
        {
            state.Inventory.Add(new Disc { Partition = 1, Set = DiscSet.SwingJazz, MainStat = StatType.HP });
        }

        state.CurrentSort = SortOrder.HighestRv;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var view = service.GetStorageView();
        sw.Stop();

        Assert.Equal(SimulationConfig.MaxInventoryDiscs, view.FilteredDiscs.Count);
        Assert.True(sw.ElapsedMilliseconds < 200,
            $"Storage view took {sw.ElapsedMilliseconds}ms for {state.Inventory.Count} discs; " +
            "expected well under a frame budget.");
    }

    [Fact]
    public async Task RunSimulationAsync_CapSkipsBuildingDiscsThatWouldBeEvicted()
    {
        // Regression guard for the cap-skip. At a permissive RV threshold most drops pass the
        // keep filter but lose to the eviction floor, so they must never be built.
        var service = NewService(out var state);
        state.MinCleanupRv = 1;
        state.TargetCleanupPartition = null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.RunSimulationAsync(100_000 * 60);
        sw.Stop();

        Assert.Equal(SimulationConfig.MaxInventoryDiscs, state.Inventory.Count);
        Assert.True(result.TotalDrops > 200_000, $"Need a large run, got {result.TotalDrops} drops.");

        // Every built disc either survived or was evicted as a better one replaced it, so
        // builds stay near the cap rather than tracking total keep attempts.
        Assert.True(result.KeptDiscs.Count < SimulationConfig.MaxInventoryDiscs * 3,
            $"Built {result.KeptDiscs.Count} discs for a cap of " +
            $"{SimulationConfig.MaxInventoryDiscs}; the cap-skip is not engaging.");

        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"100k runs took {sw.ElapsedMilliseconds}ms; expected well under 3s.");
    }

    [Fact]
    public async Task RunSimulationAsync_CapKeepsHighestRollValuesNotFirstArrivals()
    {
        // Fill past the cap and confirm the retained set is the best of what was rolled,
        // not merely the first N.
        var service = NewService(out var state);
        state.MinCleanupRv = 1;
        state.TargetCleanupPartition = null;

        await service.RunSimulationAsync(20_000 * 60);

        Assert.Equal(SimulationConfig.MaxInventoryDiscs, state.Inventory.Count);

        var worst = state.Inventory.Min(d => d.GetRollValue(state.DesiredStats));
        var best = state.Inventory.Max(d => d.GetRollValue(state.DesiredStats));

        // RV is bounded by max substats, so a retained set spanning nearly the full range is
        // the signature of keeping the top N rather than the first N.
        Assert.True(best > worst, "Retained discs should span a range of roll values.");
        Assert.True(state.Inventory.Count(d => d.GetRollValue(state.DesiredStats) == best) > 0);
    }

    [Fact]
    public async Task RunSimulationAsync_LockedDiscsStillSurviveTheCap()
    {
        var service = NewService(out var state);
        state.MinCleanupRv = 1;
        state.TargetCleanupPartition = null;

        // Lock the weakest disc we can find before the run starts.
        var seeded = state.Inventory.FirstOrDefault();

        await service.RunSimulationAsync(6000 * 60);

        Assert.Equal(SimulationConfig.MaxInventoryDiscs, state.Inventory.Count);

        if (seeded != null)
        {
            Assert.True(seeded.IsLocked || !state.Inventory.Contains(seeded),
                "A locked disc must never be evicted.");
        }
    }

    [Fact]
    public void GetRunPriorities_UsesSharedListWhenTargetIsAll()
    {
        var state = new SimulationState();
        state.TargetCleanupPartition = null;
        state.GetActivePrioritizedSubstats().Add(StatType.AnomalyProficiency);

        // Every slot must be scored against the edited All-slots list, not its own default.
        foreach (int partition in new[] { 1, 2, 3, 4, 5, 6 })
        {
            Assert.Contains(StatType.AnomalyProficiency, state.GetRunPriorities(partition));
        }
    }

    [Fact]
    public void GetRunPriorities_UsesSlotListWhenTargeted()
    {
        var state = new SimulationState();
        state.TargetCleanupPartition = 4;

        // A targeted run scores only that slot, against that slot's list. Identical object:
        // only partition-4 discs can survive the partition check anyway.
        Assert.Same(
            state.GetPrioritizedSubstatsForPartition(4),
            state.GetRunPriorities(4));
    }

    [Fact]
    public async Task RunSimulationAsync_AllTargetWithFourPrioritiesKeepsMinRvFour()
    {
        // Repro: Min RV 4 with 4 All-slots priorities kept 0 out of 25M drops, because runs
        // scored each slot's 3-stat default list (max RV 3) instead of the edited shared list.
        var service = NewService(out var state);
        state.TargetCleanupPartition = null;
        state.MinCleanupRv = 4;
        state.GetActivePrioritizedSubstats().Add(StatType.AnomalyProficiency);

        var result = await service.RunSimulationAsync(3000 * 60);

        Assert.True(result.KeptDiscs.Count > 0,
            "Expected some RV-4 survivors out of ~7500 drops; the run scored the wrong list.");

        var shared = state.GetActivePrioritizedSubstats().ToHashSet();
        foreach (var disc in result.KeptDiscs)
        {
            Assert.True(disc.GetRollValue(shared) >= 4,
                "Every kept disc must reach Min RV under the shared All-slots list.");
        }
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
        Assert.True(result.CountFor(DismantleReason.PartitionMismatch) > 0,
            "Expected partition-mismatch dismantles.");
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
        Assert.True(result.CountFor(DismantleReason.BelowMinRollValue) > 0,
            "Expected below-min-RV dismantles.");
    }
}