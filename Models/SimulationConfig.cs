namespace DiscDriveSimulator.Models;

public static class SimulationConfig
{
    public const int BatteryPerRun = 60;
    public const int MinDropsPerRun = 2;
    public const int MaxDropsPerRun = 3;
    public const int MasterCopiesPerCraft = 6;

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
