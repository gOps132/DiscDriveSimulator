namespace DiscDriveSimulator.Models;

public enum StatType
{
    // Flat
    HP,
    ATK,
    DEF,

    // Percentage
    HPPercent,
    ATKPercent,
    DEFPercent,

    // Crit
    CritRate,
    CritDmg,

    // Special
    AnomalyProficiency,
    AnomalyMastery,
    Impact,
    EnergyRegen,
    PenRatio,
    PEN,

    // Elemental DMG
    PhysicalDmg,
    FireDmg,
    IceDmg,
    ElectricDmg,
    EtherDmg
}

public enum DiscSet
{
    SetAlpha,
    SetBeta,
    WoodpeckerElectro,
    PolarMetal,
    ShockstarDisco,
    ThunderMetal,
    FangedMetal,
    FreedomBlues,
    PufferElectro,
    ChaosJazz,
    HormonePunk,
    SoulRock,
    SwingJazz,
    BranchBladeSong
}
