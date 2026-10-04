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

    // Default prioritized substats, shared so the fast path can build tables without state.
    public static HashSet<StatType> PrioritizedFor(int partition)
    {
        return new HashSet<StatType>
        {
            StatType.CritRate,
            StatType.CritDmg,
            StatType.ATKPercent
        };
    }

    // Priorities a run actually scores a partition against.
//
// When a target partition is set, that slot's own list applies — and since only that slot
// can survive the partition check, per-partition scoring and active-list scoring coincide.
// When the target is All (null), every slot is scored, so the shared All-slots list (key 0,
// the one the console editor shows) must apply to all of them. Scoring per-partition lists
// there silently ignores what the player configured: with 4 priorities edited but 3 in each
// slot list, max RV is 3, so a Min RV of 4 keeps nothing out of millions of drops.
    public List<StatType> GetRunPriorities(int partition)
    {
        if (TargetCleanupPartition == null) return GetActivePrioritizedSubstats();
        return GetPrioritizedSubstatsForPartition(partition);
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
    // Whether the simulation auto-dismantles drops that miss the current RV threshold.
    //
    // Always true in normal play: the fast path only pays when it is on, and without it long
    // runs exhaust the heap. Kept as a serialized property so existing saves deserialize;
    // nothing sets it to false.
    public bool AutoDismantleOnSim
    {
        get => true;
        set { /* forced on; see remarks */ }
    }
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

// Everything the storage grid needs about the whole inventory, computed in one pass.
// FilteredDiscs are matching discs in sort order; both counts cover unlocked discs only.
public record StorageView(
    List<Disc> FilteredDiscs,
    int NonMatchingDismantleableCount,
    int SelectedCount);

public class CleanupDropRecord
{
    public Disc? Disc { get; set; }
    public bool IsKept { get; set; }
    public int RollValue { get; set; }
    public string DismantleReason { get; set; } = string.Empty;
}

public class SimulationRunResult
{
    public int Runs { get; set; }
    public int BatterySpent { get; set; }
    // Total discs rolled (kept + dismantled). Use for DROPS display.
    public int TotalDrops { get; set; }
    // Kept discs only; dismantled discs are not materialized, to bound memory and DOM.
    public List<Disc> GeneratedDiscs { get; set; } = new();
    public List<Disc> KeptDiscs { get; set; } = new();
    // Kept records only; dismantles are aggregated into counts.
    public List<CleanupDropRecord> DropRecords { get; set; } = new();
    public int DismantledCount { get; set; }

    // Per-reason dismantle counts, indexed by DismantleReason.
    //
    // A fixed array, not a string-keyed dictionary: the simulation bumps this per dropped disc,
    // and a dictionary allocated on nearly every iteration of a long run.
    public int[] DismantleReasonCounts { get; set; } = new int[SimulationConfig.DismantleReasonCount];

    public int AutoCraftedCount { get; set; }

    // Discs dropped per partition, kept or not. Indexed by partition (index 0 unused).
    public int[] DropCountsByPartition { get; set; } = new int[7];

    // Readable dismantle reasons, built on demand rather than maintained per drop.
    public Dictionary<string, int> DismantleReasonSummary
    {
        get
        {
            var summary = new Dictionary<string, int>();
            foreach (DismantleReason reason in Enum.GetValues<DismantleReason>())
            {
                int count = CountFor(reason);
                if (count > 0) summary[Describe(reason)] = count;
            }
            return summary;
        }
    }

    public int CountFor(DismantleReason reason) =>
        (int)reason >= 0 && (int)reason < DismantleReasonCounts.Length
            ? DismantleReasonCounts[(int)reason]
            : 0;

    public void RecordDismantle(DismantleReason reason)
    {
        if ((int)reason >= 0 && (int)reason < DismantleReasonCounts.Length)
        {
            DismantleReasonCounts[(int)reason]++;
        }
    }

    // Human-readable reason text.
    public static string Describe(DismantleReason reason) => reason switch
    {
        DismantleReason.PartitionMismatch => "Partition mismatch",
        DismantleReason.BelowMinRollValue => "Below min roll value",
        DismantleReason.InventoryFullAllLocked => "Inventory full (all retained discs locked)",
        DismantleReason.EvictedForCap => "Evicted to make room",
        _ => "Unknown"
    };

    public double AverageCv => KeptDiscs.Count > 0 ? Math.Round(KeptDiscs.Average(d => d.GetCritValue()), 1) : 0;
}


