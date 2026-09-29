using System.Text.Json.Serialization;

namespace DiscDriveSimulator.Models;

public class SubstatRoll
{
    public StatType Stat { get; set; }
    public int RollHits { get; set; } = 1;
    public double Value { get; set; }

    public SubstatRoll() { }

    public SubstatRoll(StatType stat, double value, int rollHits = 1)
    {
        Stat = stat;
        Value = value;
        RollHits = rollHits;
    }
}

public class Disc
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DiscSet Set { get; set; }
    public int Partition { get; set; } // 1 to 6
    public StatType MainStat { get; set; }
    public double MainStatValue { get; set; }
    public List<SubstatRoll> Substats { get; set; } = new();
    public int InitialSubstatCount { get; set; } = 3;
    public bool IsLocked { get; set; }
    public bool IsSelected { get; set; }
    public bool IsCrafted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public double GetCritValue()
    {
        double cv = 0;
        foreach (var sub in Substats)
        {
            if (sub.Stat == StatType.CritRate)
            {
                cv += sub.Value * 2.0;
            }
            else if (sub.Stat == StatType.CritDmg)
            {
                cv += sub.Value;
            }
        }

        // Slot 4 Main stat contribution
        if (Partition == 4)
        {
            if (MainStat == StatType.CritRate)
            {
                cv += MainStatValue * 2.0;
            }
            else if (MainStat == StatType.CritDmg)
            {
                cv += MainStatValue;
            }
        }

        return Math.Round(cv, 1);
    }

    public int GetRollValue(HashSet<StatType>? desiredStats)
    {
        if (desiredStats == null || desiredStats.Count == 0) return 0;
        int rv = 0;
        foreach (var sub in Substats)
        {
            if (desiredStats.Contains(sub.Stat))
            {
                rv += sub.RollHits;
            }
        }
        return rv;
    }

    public static bool IsPercentStat(StatType stat)
    {
        return stat switch
        {
            StatType.HPPercent or
            StatType.ATKPercent or
            StatType.DEFPercent or
            StatType.CritRate or
            StatType.CritDmg or
            StatType.AnomalyMastery or
            StatType.Impact or
            StatType.EnergyRegen or
            StatType.PenRatio or
            StatType.PhysicalDmg or
            StatType.FireDmg or
            StatType.IceDmg or
            StatType.ElectricDmg or
            StatType.EtherDmg => true,
            _ => false
        };
    }

    public static string FormatStatName(StatType stat)
    {
        return stat switch
        {
            StatType.HPPercent => "HP%",
            StatType.ATKPercent => "ATK%",
            StatType.DEFPercent => "DEF%",
            StatType.CritRate => "CRIT Rate",
            StatType.CritDmg => "CRIT DMG",
            StatType.AnomalyProficiency => "Anomaly Proficiency",
            StatType.AnomalyMastery => "Anomaly Mastery",
            StatType.Impact => "Impact",
            StatType.EnergyRegen => "Energy Regen",
            StatType.PenRatio => "PEN Ratio",
            StatType.PEN => "PEN",
            StatType.PhysicalDmg => "Physical DMG%",
            StatType.FireDmg => "Fire DMG%",
            StatType.IceDmg => "Ice DMG%",
            StatType.ElectricDmg => "Electric DMG%",
            StatType.EtherDmg => "Ether DMG%",
            _ => stat.ToString()
        };
    }

    public static string FormatStatValue(StatType stat, double val)
    {
        return IsPercentStat(stat) ? $"{val:0.0}%" : $"{Math.Round(val)}";
    }
}
