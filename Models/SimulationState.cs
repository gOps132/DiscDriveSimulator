namespace DiscDriveSimulator.Models;

public enum SortOrder
{
    HighestCv,
    HighestRv,
    PartitionAsc,
    PartitionDesc,
    Newest
}

public class SimulationState
{
    // Counters
    public int TotalRuns { get; set; }
    public int TotalBatterySpent { get; set; }
    public int TotalDiscsDropped { get; set; }
    public int TotalDiscsDismantled { get; set; }
    public int TotalDiscsCrafted { get; set; }

    // Inventory & Currency
    public int BatteryCharge { get; set; } = 240;
    public int EtherBatteries { get; set; } = 12;
    public int HiFiMasterCopies { get; set; } = 60;
    public List<Disc> Inventory { get; set; } = new();

    // Routine Cleanup Settings
    public string SelectedCleanupStageId { get; set; } = "stage_1";
    public bool WithDailyCoffee { get; set; } = false;
    public int? TargetCleanupPartition { get; set; } = null; // null = All Partitions

    // Key 0 = "All", Keys 1..6 = Partitions 1..6
    public Dictionary<int, List<StatType>> PartitionPrioritizedSubstats { get; set; } = new()
    {
        [0] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
        [1] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
        [2] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
        [3] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
        [4] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
        [5] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
        [6] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent }
    };

    public List<StatType> PrioritizedSubstats
    {
        get => GetActivePrioritizedSubstats();
        set
        {
            if (value != null)
            {
                int key = TargetCleanupPartition ?? 0;
                PartitionPrioritizedSubstats[key] = value;
            }
        }
    }

    public List<StatType> GetActivePrioritizedSubstats()
    {
        int key = TargetCleanupPartition ?? 0;
        if (!PartitionPrioritizedSubstats.TryGetValue(key, out var list) || list == null)
        {
            list = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent };
            PartitionPrioritizedSubstats[key] = list;
        }
        SanitizePriorities();
        return list;
    }

    public List<StatType> GetPrioritizedSubstatsForPartition(int? partition)
    {
        SanitizePriorities();
        int key = partition ?? 0;
        if (PartitionPrioritizedSubstats.TryGetValue(key, out var list) && list != null && list.Count > 0)
        {
            return list;
        }
        if (PartitionPrioritizedSubstats.TryGetValue(0, out var fallback) && fallback != null && fallback.Count > 0)
        {
            return fallback;
        }
        return new List<StatType>();
    }

    public void SanitizePriorities()
    {
        if (PartitionPrioritizedSubstats.TryGetValue(1, out var p1))
            p1?.RemoveAll(s => s == StatType.HP);
        if (PartitionPrioritizedSubstats.TryGetValue(2, out var p2))
            p2?.RemoveAll(s => s == StatType.ATK);
        if (PartitionPrioritizedSubstats.TryGetValue(3, out var p3))
            p3?.RemoveAll(s => s == StatType.DEF);
    }

    public int MinCleanupRv { get; set; } = 2;

    // Automation Settings
    public bool AutoDismantleOnSim { get; set; } = false;
    public bool AutoCraftOnSim { get; set; } = false;
    public int AutoCraftPartition { get; set; } = 4;

    // Crafting target in Bardic Needle
    public int? SelectedCraftPartition { get; set; } = null;
    public DiscSet SelectedCraftSet { get; set; } = DiscSet.SwingJazz;

    // Desired Substats for RV
    public HashSet<StatType> DesiredStats { get; set; } = new()
    {
        StatType.CritRate,
        StatType.CritDmg,
        StatType.ATKPercent
    };

    // Filter controls
    public int? FilterPartition { get; set; } = null;
    public DiscSet? FilterSet { get; set; } = null;
    public StatType? FilterMainStat { get; set; } = null;
    public bool? FilterLock { get; set; } = null;
    public int MinRv { get; set; } = 0;
    public double MinCv { get; set; } = 0;
    public SortOrder CurrentSort { get; set; } = SortOrder.HighestCv;

    public void Reset()
    {
        TotalRuns = 0;
        TotalBatterySpent = 0;
        TotalDiscsDropped = 0;
        TotalDiscsDismantled = 0;
        TotalDiscsCrafted = 0;
        HiFiMasterCopies = 0;
        Inventory.Clear();
        TargetCleanupPartition = null;
        PartitionPrioritizedSubstats = new()
        {
            [0] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
            [1] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
            [2] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
            [3] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
            [4] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
            [5] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent },
            [6] = new() { StatType.CritRate, StatType.CritDmg, StatType.ATKPercent }
        };
        MinCleanupRv = 2;
        FilterPartition = null;
        FilterSet = null;
        FilterMainStat = null;
        FilterLock = null;
        CurrentSort = SortOrder.HighestCv;
    }
}

public class CleanupDropRecord
{
    public Disc Disc { get; set; } = null!;
    public bool IsKept { get; set; }
    public int RollValue { get; set; }
    public string DismantleReason { get; set; } = string.Empty;
}

public class SimulationRunResult
{
    public int Runs { get; set; }
    public int BatterySpent { get; set; }
    public List<Disc> GeneratedDiscs { get; set; } = new();
    public List<Disc> KeptDiscs { get; set; } = new();
    public List<CleanupDropRecord> DropRecords { get; set; } = new();
    public int DismantledCount { get; set; }
    public int AutoCraftedCount { get; set; }

    public double AverageCv => KeptDiscs.Count > 0 ? Math.Round(KeptDiscs.Average(d => d.GetCritValue()), 1) : 0;
}


