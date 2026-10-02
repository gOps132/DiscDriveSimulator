using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public class SimulationService : ISimulationService
{
    private readonly IDiscGenerator _generator;

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

        const int yieldEveryRuns = 25;
        for (int r = 0; r < affordableRuns; r++)
        {
            ct.ThrowIfCancellationRequested();
            var drops = _generator.GenerateRunDrops(stage.Set1, stage.Set2);
            State.TotalDiscsDropped += drops.Count;
            result.TotalDrops += drops.Count;

            foreach (var disc in drops)
            {
                ProcessNewDisc(disc, result);
            }

            if ((r + 1) % yieldEveryRuns == 0)
            {
                progress?.Report((double)(r + 1) / affordableRuns);
                // Yield to the WASM UI thread so progress paints instead of freezing.
                await Task.Yield();
            }
        }

        progress?.Report(1.0);
        NotifyStateChanged();
        return result;
    }

    private void ProcessNewDisc(Disc disc, SimulationRunResult? result = null)
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
