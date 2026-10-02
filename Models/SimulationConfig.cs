namespace DiscDriveSimulator.Models;

/// <summary>
/// Why a drop was dismantled.
/// </summary>
/// <remarks>
/// An enum with a fixed-size tally, not a string-keyed dictionary. The hot loop bumps this
/// once per dropped disc — millions of times on a long run — and string keys with
/// interpolated messages allocated on nearly every iteration. That cost was the single
/// largest remaining per-drop expense once sampling removed disc construction.
/// </remarks>
public enum DismantleReason
{
    /// <summary>No reason recorded. Kept drops use this.</summary>
    None = 0,

    /// <summary>Partition did not match the run's target partition.</summary>
    PartitionMismatch,

    /// <summary>Roll value fell below the configured minimum.</summary>
    BelowMinRollValue,

    /// <summary>Inventory was full and every retained disc was locked, so none could be evicted.</summary>
    InventoryFullAllLocked,

    /// <summary>A disc was evicted to make room for a better one. Counts as currency spent.</summary>
    EvictedForCap
}

public static class SimulationConfig
{
    public const int BatteryPerRun = 60;
    public const int MinDropsPerRun = 2;
    public const int MaxDropsPerRun = 3;
    public const int MasterCopiesPerCraft = 6;

    /// <summary>One counter per <see cref="DismantleReason"/>, indexed directly in the hot loop.</summary>
    public const int DismantleReasonCount = 5;

    /// <summary>Substat counts a fresh disc can roll. <see cref="DiscGenerator"/> picks uniformly from these.</summary>
    public static readonly int[] SubstatCountOptions = { 3, 4 };

    /// <summary>
    /// Main stat options per partition: fixed on 1-3, uniform over the slot pool on 4-6.
    /// </summary>
    /// <remarks>
    /// Single source of truth so the fast path and the hypergeometric sampler cannot drift
    /// apart on what a partition can roll.
    /// </remarks>
    public static IEnumerable<StatType> MainStatOptionsFor(int partition) => partition switch
    {
        1 => new[] { StatType.HP },
        2 => new[] { StatType.ATK },
        3 => new[] { StatType.DEF },
        4 => Slot4MainStats,
        5 => Slot5MainStats,
        6 => Slot6MainStats,
        _ => new[] { StatType.HP }
    };

    /// <summary>
    /// Ceiling on retained inventory: unbounded growth crashes the tab, since WASM holds every
    /// kept disc in memory and serializes them all to localStorage. Overflowing it trims the
    /// lowest-RV disc and credits a master copy.
    /// </summary>
    /// <remarks>
    /// Settable rather than <c>const</c> as a test seam: with the cap active only discs
    /// beating the eviction floor survive, so the RV distribution tests would measure that
    /// bias instead of the sampler. Tests must restore it.
    /// </remarks>
    public static int MaxInventoryDiscs { get; set; } = 5000;

    // Fixed base main stats (Level 0 S-Rank values)
    public static double GetDefaultMainStatValue(int partition, StatType stat)
    {
        return partition switch
        {
            1 => 440, // Flat HP
            2 => 62,  // Flat ATK
            3 => 32,  // Flat DEF
            4 => stat switch
            {
                StatType.CritRate => 4.8,
                StatType.CritDmg => 9.6,
                StatType.HPPercent => 6.0,
                StatType.ATKPercent => 6.0,
                StatType.DEFPercent => 9.6,
                StatType.AnomalyProficiency => 18.0,
                _ => 6.0
            },
            5 => stat switch
            {
                StatType.PenRatio => 4.8,
                StatType.HPPercent => 6.0,
                StatType.ATKPercent => 6.0,
                StatType.DEFPercent => 9.6,
                _ => 6.0 // Elemental DMG%
            },
            6 => stat switch
            {
                StatType.AnomalyMastery => 6.0,
                StatType.Impact => 3.6,
                StatType.EnergyRegen => 12.0,
                StatType.HPPercent => 6.0,
                StatType.ATKPercent => 6.0,
                StatType.DEFPercent => 9.6,
                _ => 6.0
            },
            _ => 0
        };
    }

    // Fixed substat step values per roll
    public static readonly Dictionary<StatType, double> SubstatRollValues = new()
    {
        { StatType.CritRate, 2.4 },
        { StatType.CritDmg, 4.8 },
        { StatType.ATKPercent, 3.0 },
        { StatType.DEFPercent, 4.8 },
        { StatType.HPPercent, 3.0 },
        { StatType.ATK, 19.0 },
        { StatType.DEF, 15.0 },
        { StatType.HP, 112.0 },
        { StatType.AnomalyProficiency, 9.0 },
        { StatType.PEN, 9.0 }
    };

    // Substats available in the general pool
    public static readonly StatType[] SubstatPool = new[]
    {
        StatType.HP,
        StatType.ATK,
        StatType.DEF,
        StatType.HPPercent,
        StatType.ATKPercent,
        StatType.DEFPercent,
        StatType.CritRate,
        StatType.CritDmg,
        StatType.AnomalyProficiency,
        StatType.PEN
    };

    // Substats available for a given partition (excluding fixed main stats on slots 1-3)
    public static IEnumerable<StatType> GetValidSubstatPool(int? partition)
    {
        return partition switch
        {
            1 => SubstatPool.Where(st => st != StatType.HP),
            2 => SubstatPool.Where(st => st != StatType.ATK),
            3 => SubstatPool.Where(st => st != StatType.DEF),
            _ => SubstatPool
        };
    }

    // Slot 4 Main stat pool
    public static readonly StatType[] Slot4MainStats = new[]
    {
        StatType.HPPercent,
        StatType.ATKPercent,
        StatType.DEFPercent,
        StatType.CritRate,
        StatType.CritDmg,
        StatType.AnomalyProficiency
    };

    // Slot 5 Main stat pool
    public static readonly StatType[] Slot5MainStats = new[]
    {
        StatType.HPPercent,
        StatType.ATKPercent,
        StatType.DEFPercent,
        StatType.PenRatio,
        StatType.PhysicalDmg,
        StatType.FireDmg,
        StatType.IceDmg,
        StatType.ElectricDmg,
        StatType.EtherDmg
    };

    // Slot 6 Main stat pool
    public static readonly StatType[] Slot6MainStats = new[]
    {
        StatType.HPPercent,
        StatType.ATKPercent,
        StatType.DEFPercent,
        StatType.AnomalyMastery,
        StatType.Impact,
        StatType.EnergyRegen
    };
}
