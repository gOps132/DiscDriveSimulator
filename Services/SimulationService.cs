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

    public void RunSimulation(int batteryCharge)
    {
        if (batteryCharge < SimulationConfig.BatteryPerRun) return;

        int runs = batteryCharge / SimulationConfig.BatteryPerRun;
        int batteryUsed = runs * SimulationConfig.BatteryPerRun;

        State.TotalRuns += runs;
        State.TotalBatterySpent += batteryUsed;

        for (int r = 0; r < runs; r++)
        {
            var drops = _generator.GenerateRunDrops();
            State.TotalDiscsDropped += drops.Count;

            foreach (var disc in drops)
            {
                ProcessNewDisc(disc);
            }
        }

        // Auto-craft loop if enabled
        if (State.AutoCraftOnSim)
        {
            // Keep crafting while enough copies exist
            while (State.HiFiMasterCopies >= SimulationConfig.MasterCopiesPerCraft)
            {
                State.HiFiMasterCopies -= SimulationConfig.MasterCopiesPerCraft;
                State.TotalDiscsCrafted++;

                var crafted = _generator.GenerateDisc(State.AutoCraftPartition, null, isCrafted: true);
                ProcessNewDisc(crafted);
            }
        }

        NotifyStateChanged();
    }

    private void ProcessNewDisc(Disc disc)
    {
        if (State.AutoDismantleOnSim && !MatchesFilter(disc))
        {
            State.HiFiMasterCopies++;
            State.TotalDiscsDismantled++;
        }
        else
        {
            State.Inventory.Add(disc);
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

    public Disc? CraftSingle(int partition)
    {
        if (State.HiFiMasterCopies < SimulationConfig.MasterCopiesPerCraft) return null;

        State.HiFiMasterCopies -= SimulationConfig.MasterCopiesPerCraft;
        State.TotalDiscsCrafted++;

        var disc = _generator.GenerateDisc(partition, null, isCrafted: true);
        State.Inventory.Add(disc);

        NotifyStateChanged();
        return disc;
    }

    public int CraftMultiple(int partition, int count)
    {
        int crafted = 0;
        for (int i = 0; i < count; i++)
        {
            if (CraftSingle(partition) == null) break;
            crafted++;
        }
        return crafted;
    }

    public int CraftMax(int partition)
    {
        int maxPossible = State.HiFiMasterCopies / SimulationConfig.MasterCopiesPerCraft;
        return CraftMultiple(partition, maxPossible);
    }

    public bool MatchesFilter(Disc disc)
    {
        if (State.FilterPartition.HasValue && disc.Partition != State.FilterPartition.Value)
            return false;

        if (State.FilterSet.HasValue && disc.Set != State.FilterSet.Value)
            return false;

        if (State.FilterMainStat.HasValue && disc.MainStat != State.FilterMainStat.Value)
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
