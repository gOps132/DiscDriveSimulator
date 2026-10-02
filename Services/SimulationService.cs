using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public class SimulationService : ISimulationService
{
    private readonly IDiscGenerator _generator;
    /// <summary>
    /// RNG used only for the fast-path Roll Value sample, kept separate from the generator's
    /// stream so sampling RV never perturbs disc generation.
    /// </summary>
    private readonly Random _rng = new();

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

                if (sampler is not null)
                {
                    // Fast path: decide keep/dismantle from a sampled Roll Value before
                    // building anything. Costs one uniform double + a binary search, versus
                    // a Guid, a LINQ-filtered pool, a Fisher-Yates pass and three to four
                    // SubstatRoll allocations for a disc that is about to be thrown away.
                    int partition = _generator.NextPartition();
                    result.DropCountsByPartition.TryGetValue(partition, out int seen);
                    result.DropCountsByPartition[partition] = seen + 1;

                    if (TryFastPathDecide(partition, set, result, sampler, ref cachedWeakest,
                            out int sampledRv, out StatType mainStat))
                    {
                        // Dismantled: nothing was allocated for this drop beyond the counters.
                        continue;
                    }

                    // Kept: build a disc whose substats are conditioned on the RV we already
                    // sampled, so the disc's real RV is the one we decided on.
                    var keptDisc = _generator.GenerateDiscWithRollValue(partition, set,
                        mainStat, sampledRv, sampler.PrioritizedByPartition[partition]);
                    MaterializeKeptDisc(keptDisc, result, partition);
                }
                else
                {
                    var disc = _generator.GenerateDisc(set: set);
                    ProcessNewDisc(disc, ref cachedWeakest, result);
                }
            }

            if ((r + 1) % yieldEveryRuns == 0)
            {
                // Drop the cached pointer so the next overflow rescans.
                cachedWeakest = null;

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

    /// <summary>
    /// Builds per-partition Roll Value CDFs for the current prioritized-substat setup, or
    /// <c>null</c> when the fast path cannot be used.
    /// </summary>
    /// <remarks>
    /// The fast path samples RV directly, which requires that RV's distribution depend only
    /// on <c>(partition, prioritized set, main stat, substat count)</c> — all of which are
    /// drawable before the disc is built. It is deliberately conservative: if a disc was
    /// already crafted (extra substat rolls, so <c>RollHits &gt; 1</c>), or if anything
    /// unexpected turns up, this returns <c>null</c> and callers fall back to full generation.
    /// A silently wrong RV distribution would be worse than a slow one.
    /// </remarks>
    private RvSampler? BuildFastPathSampler()
    {
        if (!State.AutoDismantleOnSim) return null;

        // Every fresh disc has exactly one roll per substat, so RollHits is always 1 and
        // RV collapses to "how many drawn substats are prioritized". Verify rather than
        // assume: if a substat ever gains extra hits, the math below is invalid.
        if (SimulationConfig.SubstatRollValues.Count != SimulationConfig.SubstatPool.Length)
        {
            throw new InvalidOperationException(
                "SubstatRollValues must cover every SubstatPool entry; the RV sampler relies on it.");
        }

        var prioritizedByPartition = new HashSet<StatType>[7];
        var cdfByPartitionMainStat = new Dictionary<(int, StatType), double[]>();

        for (int partition = 1; partition <= 6; partition++)
        {
            var prioritized = State.GetPrioritizedSubstatsForPartition(partition).ToHashSet();
            prioritizedByPartition[partition] = prioritized;

            foreach (var mainStat in MainStatOptionsFor(partition))
            {
                var pmf = HypergeometricSampler.BuildTableFor(partition, mainStat, prioritized);
                cdfByPartitionMainStat[(partition, mainStat)] =
                    HypergeometricSampler.ToCumulative(pmf);
            }
        }

        return new RvSampler(prioritizedByPartition, cdfByPartitionMainStat);
    }

    /// <summary>Every main stat a partition can roll, including a single fixed one for slots 1-3.</summary>
    private static IEnumerable<StatType> MainStatOptionsFor(int partition) => partition switch
    {
        1 => new[] { StatType.HP },
        2 => new[] { StatType.ATK },
        3 => new[] { StatType.DEF },
        4 => SimulationConfig.Slot4MainStats,
        5 => SimulationConfig.Slot5MainStats,
        6 => SimulationConfig.Slot6MainStats,
        _ => new[] { StatType.HP }
    };

    /// <summary>Per-partition RV tables keyed by partition and main stat.</summary>
    private sealed class RvSampler
    {
        public readonly HashSet<StatType>[] PrioritizedByPartition;
        public readonly Dictionary<(int, StatType), double[]> CdfByPartitionMainStat;

        public RvSampler(HashSet<StatType>[] prioritizedByPartition,
            Dictionary<(int, StatType), double[]> cdfByPartitionMainStat)
        {
            PrioritizedByPartition = prioritizedByPartition;
            CdfByPartitionMainStat = cdfByPartitionMainStat;
        }

        public double[] CdfFor(int partition, StatType mainStat) =>
            CdfByPartitionMainStat[(partition, mainStat)];
    }

    /// <summary>
    /// Fast-path keep/dismantle decision for one drop.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the drop was dismantled and already accounted for.
    /// <c>false</c> when it should be kept, in which case <paramref name="sampledRv"/> and
    /// <paramref name="mainStat"/> carry the draws the caller must use to build the disc.
    /// </returns>
    private bool TryFastPathDecide(int partition, DiscSet? set, SimulationRunResult result,
        RvSampler sampler, ref Disc? cachedWeakest, out int sampledRv, out StatType mainStat)
    {
        // The partition check is the cheapest possible reject and needs no RV draw, so do
        // it before touching the sampler.
        if (State.TargetCleanupPartition.HasValue && partition != State.TargetCleanupPartition.Value)
        {
            sampledRv = 0;
            mainStat = default;
            CountDismantle(result,
                $"Partition {partition} mismatch (Target: {State.TargetCleanupPartition.Value})");
            return true;
        }

        // Main stat first: it decides which RV distribution applies, since a prioritized
        // stat that becomes the main stat is removed from the substat pool.
        mainStat = _generator.NextMainStat(partition);

        // Separate RNG stream, so sampling RV never perturbs disc generation.
        sampledRv = HypergeometricSampler.Sample(sampler.CdfFor(partition, mainStat), _rng.NextDouble());

        if (sampledRv < State.MinCleanupRv)
        {
            CountDismantle(result, $"RV {sampledRv} below Min RV {State.MinCleanupRv}");
            return true;
        }

        if (State.Inventory.Count >= SimulationConfig.MaxInventoryDiscs)
        {
            // Cap hit. Evict the weakest retained disc so the cap keeps the best.
            if (cachedWeakest == null || cachedWeakest.IsLocked || !State.Inventory.Contains(cachedWeakest))
            {
                cachedWeakest = FindWeakestDisc();
            }

            if (cachedWeakest != null)
            {
                State.Inventory.Remove(cachedWeakest);
                State.HiFiMasterCopies++;
                State.TotalDiscsDismantled++;
                cachedWeakest = null;
            }
            else
            {
                CountDismantle(result, "Inventory cap reached (all retained discs locked)");
                return true;
            }
        }

        return false;
    }

    private void CountDismantle(SimulationRunResult result, string reason)
    {
        State.HiFiMasterCopies++;
        State.TotalDiscsDismantled++;
        result.DismantledCount++;
        result.DismantleReasonSummary.TryGetValue(reason, out int n);
        result.DismantleReasonSummary[reason] = n + 1;
    }

    /// <summary>
    /// Adds a disc the fast path decided to keep. RV is taken from the sampled value
    /// rather than recomputed, keeping the fast path single-pass.
    /// </summary>
    private void MaterializeKeptDisc(Disc disc, SimulationRunResult result, int partition)
    {
        State.Inventory.Add(disc);
        result.KeptDiscs.Add(disc);
        result.GeneratedDiscs.Add(disc);
        result.DropRecords.Add(new CleanupDropRecord
        {
            Disc = disc,
            IsKept = true,
            // Derived from the built substats rather than the sampled RV: the real disc is
            // the source of truth once it exists, and it keeps DropRecords consistent with
            // the inventory a player can inspect.
            RollValue = disc.GetRollValue(State.GetPrioritizedSubstatsForPartition(partition)),
            DismantleReason = string.Empty
        });
    }

    private void ProcessNewDisc(Disc disc, ref Disc? cachedWeakest, SimulationRunResult? result = null)
    {
        var priorities = State.GetPrioritizedSubstatsForPartition(disc.Partition);
        int rv = disc.GetRollValue(priorities);
        bool isKept = true;
        string dismantleReason = string.Empty;

        if (State.AutoDismantleOnSim)
        {
            if (State.TargetCleanupPartition.HasValue && disc.Partition != State.TargetCleanupPartition.Value)
            {
                isKept = false;
                dismantleReason = $"Partition {disc.Partition} mismatch (Target: {State.TargetCleanupPartition.Value})";
            }
            else if (rv < State.MinCleanupRv)
            {
                isKept = false;
                dismantleReason = $"RV {rv} below Min RV {State.MinCleanupRv}";
            }
        }

        if (isKept && State.Inventory.Count >= SimulationConfig.MaxInventoryDiscs)
        {
            // Inventory is full. Evict the weakest retained disc first (never a locked
            // one) so the cap keeps the best discs instead of blowing the WASM heap.
            // Uses the per-batch cached candidate; rescans only if it's now null/locked.
            if (cachedWeakest == null || cachedWeakest.IsLocked || !State.Inventory.Contains(cachedWeakest))
            {
                cachedWeakest = FindWeakestDisc();
            }

            if (cachedWeakest != null)
            {
                State.Inventory.Remove(cachedWeakest);
                State.HiFiMasterCopies++;
                State.TotalDiscsDismantled++;
                cachedWeakest = null;
            }
            else
            {
                // Everything retained is locked — can't trim. Drop the new disc instead.
                isKept = false;
                dismantleReason = "Inventory cap reached (all retained discs locked)";
            }
        }

        if (isKept)
        {
            State.Inventory.Add(disc);
            if (result != null)
            {
                result.KeptDiscs.Add(disc);
                result.GeneratedDiscs.Add(disc);
                result.DropRecords.Add(new CleanupDropRecord
                {
                    Disc = disc,
                    IsKept = true,
                    RollValue = rv,
                    DismantleReason = string.Empty
                });
            }
        }
        else
        {
            State.HiFiMasterCopies++;
            State.TotalDiscsDismantled++;
            if (result != null)
            {
                result.DismantledCount++;
                if (!string.IsNullOrEmpty(dismantleReason))
                {
                    result.DismantleReasonSummary.TryGetValue(dismantleReason, out int n);
                    result.DismantleReasonSummary[dismantleReason] = n + 1;
                }
                // Intentionally NOT storing the Disc object or DropRecord for dismantled
                // drops — compute-first, render-kept-only. This bounds WASM memory and DOM.
            }
        }
    }

    /// <summary>
    /// Finds the single lowest-RV unlocked disc in inventory, or null if all are locked.
    /// Called at most once per batch when the inventory cap is hit.
    /// </summary>
    private Disc? FindWeakestDisc()
    {
        Disc? weakest = null;
        int weakestRv = int.MaxValue;

        foreach (var d in State.Inventory)
        {
            if (d.IsLocked) continue;
            int rv = d.GetRollValue(State.DesiredStats);
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
        var query = State.Inventory.Where(MatchesFilter);

        query = State.CurrentSort switch
        {
            SortOrder.HighestCv => query.OrderByDescending(d => d.GetCritValue()),
            SortOrder.HighestRv => query.OrderByDescending(d => d.GetRollValue(State.DesiredStats)).ThenByDescending(d => d.GetCritValue()),
            SortOrder.PartitionAsc => query.OrderBy(d => d.Partition).ThenByDescending(d => d.GetCritValue()),
            SortOrder.PartitionDesc => query.OrderByDescending(d => d.Partition).ThenByDescending(d => d.GetCritValue()),
            SortOrder.Newest => query.OrderByDescending(d => d.CreatedAt),
            _ => query.OrderByDescending(d => d.GetCritValue())
        };

        return query.ToList();
    }

    public void Reset()
    {
        State.Reset();
        NotifyStateChanged();
    }

    public void NotifyStateChanged() => OnChange?.Invoke();
}
