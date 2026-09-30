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

    public int GetRollValue(IEnumerable<StatType>? desiredStats)
    {
        if (desiredStats == null) return 0;
        if (desiredStats is HashSet<StatType> set) return GetRollValue(set);
        return GetRollValue(new HashSet<StatType>(desiredStats));
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

    public static string FormatSetName(DiscSet set)
    {
        return set switch
        {
            DiscSet.SwingJazz => "Swing Jazz",
            DiscSet.ChaoticMetal => "Chaotic Metal",
            DiscSet.HormonePunk => "Hormone Punk",
            DiscSet.FangedMetal => "Fanged Metal",
            DiscSet.ShockstarDisco => "Shockstar Disco",
            DiscSet.ThunderMetal => "Thunder Metal",
            DiscSet.WoodpeckerElectro => "Woodpecker Electro",
            DiscSet.SoulRock => "Soul Rock",
            DiscSet.PufferElectro => "Puffer Electro",
            DiscSet.InfernoMetal => "Inferno Metal",
            DiscSet.FreedomBlues => "Freedom Blues",
            DiscSet.PolarMetal => "Polar Metal",
            DiscSet.ChaosJazz => "Chaos Jazz",
            DiscSet.ProtoPunk => "Proto Punk",
            DiscSet.BranchBladeSong => "Branch and Blade Song",
            DiscSet.AstralVoice => "Astral Voice",
            DiscSet.PhaethonsMelody => "Phaethon's Melody",
            DiscSet.ShadowHarmony => "Shadow Harmony",
            DiscSet.YunkuiTales => "Yunkui Tales",
            DiscSet.KingOfTheSummit => "King of the Summit",
            DiscSet.DawnsBloom => "Dawn's Bloom",
            DiscSet.MoonlightLullaby => "Moonlight Lullaby",
            DiscSet.WhiteWaterBallad => "White Water Ballad",
            DiscSet.ShiningAria => "Shining Aria",
            DiscSet.BunnyInWonderland => "Bunny in Wonderland",
            DiscSet.NotesFromTheChained => "Notes From the Chained",
            DiscSet.TheSkyAblaze => "The Sky Ablaze",
            DiscSet.WutheringSalon => "Wuthering Salon",
            DiscSet.FeatheredFate => "Feathered Fate",
            DiscSet.ThornedRose => "Thorned Rose",
            DiscSet.SetAlpha => "Set Alpha",
            DiscSet.SetBeta => "Set Beta",
            _ => set.ToString()
        };
    }

    public static string GetDiscImagePath(DiscSet set)
    {
        return set switch
        {
            DiscSet.SwingJazz => "assets/disc/Drive_Disc_Swing_Jazz_S.webp",
            DiscSet.ChaoticMetal => "assets/disc/Drive_Disc_Chaotic_Metal_S.webp",
            DiscSet.HormonePunk => "assets/disc/Drive_Disc_Hormone_Punk_S.webp",
            DiscSet.FangedMetal => "assets/disc/Drive_Disc_Fanged_Metal_S.webp",
            DiscSet.ShockstarDisco => "assets/disc/Drive_Disc_Shockstar_Disco_S.webp",
            DiscSet.ThunderMetal => "assets/disc/Drive_Disc_Thunder_Metal_S.webp",
            DiscSet.WoodpeckerElectro => "assets/disc/Drive_Disc_Woodpecker_Electro_S.webp",
            DiscSet.SoulRock => "assets/disc/Drive_Disc_Soul_Rock_S.webp",
            DiscSet.PufferElectro => "assets/disc/Drive_Disc_Puffer_Electro_S.webp",
            DiscSet.InfernoMetal => "assets/disc/Drive_Disc_Inferno_Metal_S.webp",
            DiscSet.FreedomBlues => "assets/disc/Drive_Disc_Freedom_Blues_S.webp",
            DiscSet.PolarMetal => "assets/disc/Drive_Disc_Polar_Metal_S.webp",
            DiscSet.ChaosJazz => "assets/disc/Drive_Disc_Chaos_Jazz_S.webp",
            DiscSet.ProtoPunk => "assets/disc/Drive_Disc_Proto_Punk_S.webp",
            DiscSet.BranchBladeSong => "assets/disc/Drive_Disc_Branch_%26_Blade_Song_S.webp",
            DiscSet.AstralVoice => "assets/disc/Drive_Disc_Astral_Voice_S.webp",
            DiscSet.PhaethonsMelody => "assets/disc/Drive_Disc_Phaethon%27s_Melody_S.webp",
            DiscSet.ShadowHarmony => "assets/disc/Drive_Disc_Shadow_Harmony_S.webp",
            DiscSet.YunkuiTales => "assets/disc/Drive_Disc_Yunkui_Tales_S.webp",
            DiscSet.KingOfTheSummit => "assets/disc/Drive_Disc_King_of_the_Summit_S.webp",
            DiscSet.DawnsBloom => "assets/disc/Drive_Disc_Dawn%27s_Bloom_S.webp",
            DiscSet.MoonlightLullaby => "assets/disc/Drive_Disc_Moonlight_Lullaby_S.webp",
            DiscSet.WhiteWaterBallad => "assets/disc/Drive_Disc_White_Water_Ballad_S.webp",
            DiscSet.ShiningAria => "assets/disc/Drive_Disc_Shining_Aria_S.webp",
            DiscSet.BunnyInWonderland => "assets/disc/Drive_Disc_Bunny_in_Wonderland_S.webp",
            DiscSet.NotesFromTheChained => "assets/disc/Drive_Disc_Notes_From_the_Chained_S.webp",
            DiscSet.TheSkyAblaze => "assets/disc/Drive_Disc_The_Sky_Ablaze_S.webp",
            DiscSet.WutheringSalon => "assets/disc/Drive_Disc_Wuthering_Salon_S.webp",
            DiscSet.FeatheredFate => "assets/disc/Drive_Disc_Feathered_Fate_S.webp",
            DiscSet.ThornedRose => "assets/disc/Drive_Disc_Thorned_Rose_S.webp",
            _ => "assets/disc/Drive_Disc_Woodpecker_Electro_S.webp"
        };
    }
}
