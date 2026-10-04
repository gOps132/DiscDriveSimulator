using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public class SimulationService : ISimulationService
{
    private readonly IDiscGenerator _generator;

    // Roll value of the currently cached weakest retained disc, so the cap comparison does
    // not rescore it per drop. Reset whenever the cached disc reference is invalidated.
    private int weakestRetainedRv = int.MaxValue;

    public SimulationState State { get; private set; } = new();
    public event Action? OnChange;

    public SimulationService(IDiscGenerator generator)
    {
        _generator = generator;
    }

    public void SetState(SimulationState state)
    {
        State = state ?? new SimulationState();
        NotifyStateChanged();
    }

    public SimulationRunResult RunSimulation(int batteryCharge)
    {
        return RunSimulationAsync(batteryCharge).GetAwaiter().GetResult();
    }

    // Runs batteryCharge worth of cleanup, batching so the WASM UI thread can paint progress.
    //
    // Drops are decided by sampling Roll Value from precomputed hypergeometric tables, and
    // only kept discs are materialized. See HypergeometricSampler for why that is
    // distributionally identical to generating every disc.
    public async Task<SimulationRunResult> RunSimulationAsync(int batteryCharge, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int requestedRuns = batteryCharge / SimulationConfig.BatteryPerRun;
        int batteryNeeded = requestedRuns * SimulationConfig.BatteryPerRun;

        if (requestedRuns <= 0) return new SimulationRunResult();

        // Auto-convert Ether Batteries if battery charge is insufficient
        if (State.BatteryCharge < batteryNeeded && State.EtherBatteries > 0)
        {
            int deficit = batteryNeeded - State.BatteryCharge;
            int etherNeeded = (int)Math.Ceiling(deficit / (double)SimulationConfig.BatteryPerRun);
            int etherToUse = Math.Min(etherNeeded, State.EtherBatteries);
            State.EtherBatteries -= etherToUse;
            State.BatteryCharge += etherToUse * SimulationConfig.BatteryPerRun;
        }

        int affordableRuns = Math.Min(requestedRuns, State.BatteryCharge / SimulationConfig.BatteryPerRun);
        if (affordableRuns <= 0) return new SimulationRunResult();

        int batteryUsed = affordableRuns * SimulationConfig.BatteryPerRun;
        var result = new SimulationRunResult
        {
            Runs = affordableRuns,
            BatterySpent = batteryUsed
        };

        State.TotalRuns += affordableRuns;
        State.TotalBatterySpent += batteryUsed;
        State.BatteryCharge -= batteryUsed;

        var stage = RoutineCleanupStage.AllStages.FirstOrDefault(s => s.Id == State.SelectedCleanupStageId)
                    ?? RoutineCleanupStage.AllStages[0];

        // Batch size scales with run count so we yield a bounded ~400 times total.
        // A fixed batch (e.g. every 5 runs) floods the WASM scheduler at 1M runs:
        // 200k yields each cost a scheduler round-trip, so the work never lands.
        var yieldEveryRuns = Math.Max(1, affordableRuns / 400);
        int lastReportedPct = -1;
        // Disc evicted on the next cap overflow. Recomputed once per batch rather
        // than per kept disc: a full scan per disc would be O(cap x keptDiscs).
        Disc? cachedWeakest = null;
        var rankIndex = new InventoryRankIndex();

        var sampler = BuildFastPathSampler();

        for (int r = 0; r < affordableRuns; r++)
        {
            ct.ThrowIfCancellationRequested();

            int dropCount = _generator.NextDropCount();
            State.TotalDiscsDropped += dropCount;
            result.TotalDrops += dropCount;

            for (int i = 0; i < dropCount; i++)
            {
                var set = _generator.NextDropSet(stage.Set1, stage.Set2);

                // Decide keep/dismantle from a sampled Roll Value before building anything.
                // Costs one uniform double plus a binary search, versus a Guid, a filtered
                // pool, a Fisher-Yates pass and three to four SubstatRoll allocations for a
                // disc that is about to be thrown away.
                int partition = _generator.NextPartition();
                result.DropCountsByPartition[partition]++;

                if (TryFastPathDecide(partition, result, sampler, rankIndex, ref cachedWeakest,
                        out int sampledRv, out StatType mainStat, out int substatCount))
                {
                    // Dismantled: nothing was allocated for this drop beyond the counters.
                    continue;
                }

                // Kept: build a disc whose substats are conditioned on the RV we already
                // sampled, so the disc's real RV is the one we decided on.
                var keptDisc = _generator.GenerateDiscWithRollValue(partition, set,
                    mainStat, substatCount, sampledRv, sampler.PrioritizedByPartition[partition]);
                MaterializeKeptDisc(keptDisc, result, partition, rankIndex);
            }

            if ((r + 1) % yieldEveryRuns == 0)
            {
                // Drop the cached pointer so the next overflow re-checks the floor.
                cachedWeakest = null;
                weakestRetainedRv = int.MaxValue;

                // Throttle repaints to whole percent steps so the UI thread isn't
                // flooded with thousands of StateHasChanged calls on huge runs.
                int pct = (int)((r + 1) * 100L / affordableRuns);
                if (pct != lastReportedPct)
                {
                    lastReportedPct = pct;
                    progress?.Report((double)(r + 1) / affordableRuns);
                }
                await Task.Yield();
            }
        }

        progress?.Report(1.0);
        NotifyStateChanged();
        return result;
    }

    // Builds the Roll Value CDFs for the current prioritized-substat setup, one per
    // (partition, main stat, substat count) combination.
    //
    // The fast path samples RV directly, which requires that RV's distribution depend only on
    // those three things plus the prioritized set — all drawable before the disc is built.
    // Anything unexpected throws from RvSampler.CdfFor rather than falling back to a slower
    // path, because a silently wrong RV distribution would corrupt every statistic reported.
    private RvSampler BuildFastPathSampler()
    {

        // Every fresh disc has exactly one roll per substat, so RollHits is always 1 and
        // RV collapses to "how many drawn substats are prioritized". Verify rather than
        // assume: if a substat ever gains extra hits, the math below is invalid.
        if (SimulationConfig.SubstatRollValues.Count != SimulationConfig.SubstatPool.Length)
        {
            throw new InvalidOperationException(
                "SubstatRollValues must cover every SubstatPool entry; the RV sampler relies on it.");
        }

        var prioritizedByPartition = new HashSet<StatType>[7];
        var cdfs = new Dictionary<RvKey, double[]>();

        for (int partition = 1; partition <= 6; partition++)
        {
            var prioritized = State.GetRunPriorities(partition).ToHashSet();
            prioritizedByPartition[partition] = prioritized;

            foreach (var mainStat in SimulationConfig.MainStatOptionsFor(partition))
            {
                foreach (var substatCount in SimulationConfig.SubstatCountOptions)
                {
                    var pmf = HypergeometricSampler.BuildTableFor(
                        partition, mainStat, substatCount, prioritized);
                    cdfs[new RvKey(partition, mainStat, substatCount)] =
                        HypergeometricSampler.ToCumulative(pmf);
                }
            }
        }

        return new RvSampler(prioritizedByPartition, cdfs);
    }

    // Identifies one RV distribution: everything that determines it.
    private readonly record struct RvKey(int Partition, StatType MainStat, int SubstatCount);

    // Per-partition prioritized sets plus the RV CDFs keyed by RvKey.
    private sealed class RvSampler
    {
        public readonly HashSet<StatType>[] PrioritizedByPartition;
        private readonly Dictionary<RvKey, double[]> _cdfs;

        public RvSampler(HashSet<StatType>[] prioritizedByPartition, Dictionary<RvKey, double[]> cdfs)
        {
            PrioritizedByPartition = prioritizedByPartition;
            _cdfs = cdfs;
        }

        public double[] CdfFor(int partition, StatType mainStat, int substatCount)
        {
            if (!_cdfs.TryGetValue(new RvKey(partition, mainStat, substatCount), out var cdf))
            {
                throw new InvalidOperationException(
                    $"No RV distribution for partition {partition}, main stat {mainStat}, " +
                    $"{substatCount} substats. The sampler's tables are out of sync with " +
                    "SimulationConfig.");
            }
            return cdf;
        }
    }

    // Fast-path keep/dismantle decision for one drop. Returns true when the drop was dismantled
    // and already accounted for; false when it should be kept, in which case sampledRv and
    // mainStat carry the draws the caller needs to build the disc.
    private bool TryFastPathDecide(int partition, SimulationRunResult result, RvSampler sampler,
        InventoryRankIndex rankIndex, ref Disc? cachedWeakest,
        out int sampledRv, out StatType mainStat, out int substatCount)
    {
        // The partition check is the cheapest possible reject and needs no RV draw, so do
        // it before touching the sampler.
        if (State.TargetCleanupPartition.HasValue && partition != State.TargetCleanupPartition.Value)
        {
            sampledRv = 0;
            mainStat = default;
            substatCount = 0;
            CountDismantle(result, DismantleReason.PartitionMismatch);
            return true;
        }

        // Main stat first: it decides which RV distribution applies, since a prioritized
        // stat that becomes the main stat is removed from the substat pool.
        mainStat = _generator.NextMainStat(partition);

        // Substat count next, then RV from that specific count's table. Drawing k after RV
        // (or independently of it) would make P(RV=x | k=3) the *mixture* rather than
        // Hypergeom(n, d, 3), leaving the RV marginal correct while 3-substat discs had
        // the wrong composition.
        substatCount = _generator.NextInitialSubstatCount();

        // Drawn from the generator's stream, so disc generation stays a pure function of run
        // count: sampling one more RV cannot shift which substats a disc receives.
        sampledRv = HypergeometricSampler.Sample(
            sampler.CdfFor(partition, mainStat, substatCount), _generator.NextUniform());

        if (sampledRv < State.MinCleanupRv)
        {
            CountDismantle(result, DismantleReason.BelowMinRollValue);
            return true;
        }

        if (State.Inventory.Count >= SimulationConfig.MaxInventoryDiscs)
        {
            // At the cap, build nothing unless this drop would beat the weakest disc we
            // are holding. Previously every drop over the cap was fully materialized - Guid,
            // two pool lists, shuffles - only to be evicted immediately. At a permissive RV
            // threshold that meant tens of thousands of discs built and thrown away per run,
            // which dominated the whole simulation.
            //
            // Comparing the sampled RV is enough: the incoming disc would replace the weakest
            // only if its RV is strictly better, and no other stat affects which disc ranks
            // weakest.
            if (cachedWeakest == null)
            {
                // Rebuild lazily: an eviction changed the floor, and the index may have
                // drifted from inventory if anything was dismantled or locked mid-run.
                if (rankIndex.Count != State.Inventory.Count)
                {
                    rankIndex.Invalidate();
                    RebuildRankIndex(rankIndex);
                }

                cachedWeakest = rankIndex.PeekWeakest();
                weakestRetainedRv = cachedWeakest == null
                    ? int.MaxValue
                    : cachedWeakest.GetRollValue(
                        State.GetRunPriorities(cachedWeakest.Partition));
            }

            if (cachedWeakest == null)
            {
                // Everything retained is locked, so there is nothing we are allowed to drop.
                CountDismantle(result, DismantleReason.InventoryFullAllLocked);
                return true;
            }

            if (sampledRv <= weakestRetainedRv)
            {
                // Would be evicted immediately. Skip the build; the player still gets the
                // master copy, same as before.
                CountDismantle(result, DismantleReason.EvictedForCap);
                return true;
            }

            State.Inventory.Remove(cachedWeakest);
            rankIndex.Remove(cachedWeakest, weakestRetainedRv);
            State.HiFiMasterCopies++;
            State.TotalDiscsDismantled++;
            result.DismantledCount++;
            result.RecordDismantle(DismantleReason.EvictedForCap);

            // The floor moved, so look it up again on the next drop.
            cachedWeakest = null;
        }

        return false;
    }

    // Indexes every retained disc by roll value so cap evictions do not rescan inventory.
    private void RebuildRankIndex(InventoryRankIndex index)
    {
        foreach (var disc in State.Inventory)
        {
            index.Add(disc, disc.GetRollValue(State.GetRunPriorities(disc.Partition)));
        }
    }

    private void CountDismantle(SimulationRunResult result, DismantleReason reason)
    {
        State.HiFiMasterCopies++;
        State.TotalDiscsDismantled++;
        result.DismantledCount++;
        result.RecordDismantle(reason);
    }

    private void MaterializeKeptDisc(Disc disc, SimulationRunResult result, int partition,
        InventoryRankIndex? rankIndex = null)
    {
        State.Inventory.Add(disc);

        // Scored with the run priorities, not the slot list: under an All target those differ,
        // and the record must agree with the decision that kept the disc.
        var runPriorities = State.GetRunPriorities(partition);

        // Keep the rank index in step with inventory, otherwise its size check treats the
        // index as stale and rebuilds it on the very next drop.
        rankIndex?.Add(disc, disc.GetRollValue(runPriorities));

        result.KeptDiscs.Add(disc);
        result.GeneratedDiscs.Add(disc);
        result.DropRecords.Add(new CleanupDropRecord
        {
            Disc = disc,
            IsKept = true,
            // Derived from the built substats rather than the sampled RV: the real disc is
            // the source of truth once it exists, and it keeps DropRecords consistent with
            // the inventory a player can inspect.
            RollValue = disc.GetRollValue(runPriorities),
            DismantleReason = string.Empty
        });
    }


    // Finds the lowest-RV unlocked disc in inventory, or null if all are locked.
    //
    // Reference implementation kept for InventoryRankIndex to be validated against — the index
    // is an optimization of exactly this scan.
    //
    // Scored with the run priorities, the same measure the incoming drop's sampled RV uses,
    // so the eviction floor and the keep decision follow one rule. Comparing across partitions
    // is still apples-to-oranges in principle when slots value different stats, but with the
    // default priorities, identical across all six slots, the measures coincide.
    private Disc? FindWeakestDisc()
    {
        Disc? weakest = null;
        int weakestRv = int.MaxValue;

        foreach (var d in State.Inventory)
        {
            if (d.IsLocked) continue;
            int rv = d.GetRollValue(State.GetRunPriorities(d.Partition));
            if (rv < weakestRv)
            {
                weakestRv = rv;
                weakest = d;
            }
        }

        return weakest;
    }

    public bool DismantleDisc(Disc disc)
    {
        if (disc.IsLocked) return false;

        if (State.Inventory.Remove(disc))
        {
            State.HiFiMasterCopies++;
            State.TotalDiscsDismantled++;
            NotifyStateChanged();
            return true;
        }
        return false;
    }

    public int DismantleDiscs(IEnumerable<Disc> discs)
    {
        int dismantled = 0;
        var toDismantle = discs.Where(d => !d.IsLocked).ToList();
        foreach (var disc in toDismantle)
        {
            if (State.Inventory.Remove(disc))
            {
                State.HiFiMasterCopies++;
                State.TotalDiscsDismantled++;
                dismantled++;
            }
        }

        if (dismantled > 0)
        {
            NotifyStateChanged();
        }
        return dismantled;
    }

    public int DismantleNonMatching()
    {
        var nonMatching = State.Inventory.Where(d => !d.IsLocked && !MatchesFilter(d)).ToList();
        return DismantleDiscs(nonMatching);
    }

    public Disc? CraftSingle(int? partition = null, DiscSet? set = null)
    {
        if (State.HiFiMasterCopies < SimulationConfig.MasterCopiesPerCraft) return null;

        State.HiFiMasterCopies -= SimulationConfig.MasterCopiesPerCraft;
        State.TotalDiscsCrafted++;

        var disc = _generator.GenerateDisc(partition, set, isCrafted: true);
        State.Inventory.Add(disc);

        NotifyStateChanged();
        return disc;
    }

    public int CraftMultiple(int? partition, int count, DiscSet? set = null)
    {
        int crafted = 0;
        for (int i = 0; i < count; i++)
        {
            if (CraftSingle(partition, set) == null) break;
            crafted++;
        }
        return crafted;
    }

    public int CraftMax(int? partition, DiscSet? set = null)
    {
        int maxPossible = State.HiFiMasterCopies / SimulationConfig.MasterCopiesPerCraft;
        return CraftMultiple(partition, maxPossible, set);
    }

    public bool MatchesFilter(Disc disc)
    {
        if (State.FilterPartition.HasValue && disc.Partition != State.FilterPartition.Value)
            return false;

        if (State.FilterSet.HasValue && disc.Set != State.FilterSet.Value)
            return false;

        if (State.FilterMainStat.HasValue && disc.MainStat != State.FilterMainStat.Value)
            return false;

        if (State.FilterLock.HasValue && disc.IsLocked != State.FilterLock.Value)
            return false;

        if (State.MinRv > 0 && disc.GetRollValue(State.DesiredStats) < State.MinRv)
            return false;

        if (State.MinCv > 0 && disc.GetCritValue() < State.MinCv)
            return false;

        return true;
    }

    public List<Disc> GetFilteredDiscs()
    {
        return SortFiltered(State.Inventory.Where(MatchesFilter));
    }

    // Filter, sort, and tally the inventory in one pass.
    // The grid needs the matching discs in sort order plus two batch counts. Separate LINQ
    // chains meant five filter-and-sort passes per render on a full inventory, with the RV
    // sort calling Disc.GetRollValue twice per comparison.
    public StorageView GetStorageView()
    {
        var matches = new List<Disc>(State.Inventory.Count);
        int nonMatching = 0;
        int selected = 0;

        foreach (var disc in State.Inventory)
        {
            if (disc.IsSelected && !disc.IsLocked) selected++;

            if (!MatchesFilter(disc))
            {
                if (!disc.IsLocked) nonMatching++;
                continue;
            }

            matches.Add(disc);
        }

        return new StorageView(SortFiltered(matches), nonMatching, selected);
    }

    // Applies the current sort, computing each key once per disc rather than per comparison.
    private List<Disc> SortFiltered(IEnumerable<Disc> discs)
    {
        return State.CurrentSort switch
        {
            SortOrder.HighestRv => discs
                .OrderByDescending(d => d.GetRollValue(State.DesiredStats))
                .ThenByDescending(d => d.GetCritValue())
                .ToList(),

            SortOrder.PartitionAsc => discs
                .OrderBy(d => d.Partition)
                .ThenByDescending(d => d.GetCritValue())
                .ToList(),

            SortOrder.PartitionDesc => discs
                .OrderByDescending(d => d.Partition)
                .ThenByDescending(d => d.GetCritValue())
                .ToList(),

            SortOrder.Newest => discs
                .OrderByDescending(d => d.CreatedAt)
                .ToList(),

            // HighestCv is also the default, so a full storage pays for this on open.
            _ => discs
                .OrderByDescending(d => d.GetCritValue())
                .ToList()
        };
    }

    public void Reset()
    {
        State.Reset();
        NotifyStateChanged();
    }

    public void NotifyStateChanged() => OnChange?.Invoke();

    // Indexes inventory by roll value so cap evictions never rescan it.
    //
    // Roll value is bounded by the substat cap (4), so a few buckets give O(1) lookups.
    // Count is compared against inventory size to detect drift, since a disc can be
    // dismantled or locked elsewhere between drops.
    private sealed class InventoryRankIndex
    {
    private const int BucketCount = 5; // RV 0..4
    private readonly List<Disc>?[] _buckets = new List<Disc>?[BucketCount];
    private readonly HashSet<Disc> _indexed = new();

    // Disc count currently indexed. Compared against inventory size to detect drift.
    public int Count => _indexed.Count;

    public void Add(Disc disc, int rv)
    {
        if (!_indexed.Add(disc)) return;

        var bucket = _buckets[rv];
        if (bucket == null) _buckets[rv] = bucket = new List<Disc>();
        bucket.Add(disc);
    }

    // Returns the lowest-RV unlocked disc without removing it, or null if all are locked.
    //
    // Deliberately non-destructive. The caller only removes the disc once it is actually
    // evicted; if this mutated the index, its size would drift from inventory's and every
    // subsequent drop would trigger a full rebuild.
    public Disc? PeekWeakest()
    {
        for (int rv = 0; rv < BucketCount; rv++)
        {
            var bucket = _buckets[rv];
            if (bucket == null) continue;

            // Locked discs stay in the index: the player may unlock them later, and a locked
            // disc must never be evicted.
            for (int i = 0; i < bucket.Count; i++)
            {
                if (!bucket[i].IsLocked) return bucket[i];
            }
        }

        return null;
    }

    // Drops a disc from the index. rv is its bucket, which the caller already has cached.
    // Call only when the disc also leaves inventory, so the index size keeps matching.
    public void Remove(Disc disc, int rv)
    {
        if (!_indexed.Remove(disc)) return;
        _buckets[rv]?.Remove(disc);
    }

    public void Invalidate()
    {
        Array.Clear(_buckets);
        _indexed.Clear();
    }
    }
}
