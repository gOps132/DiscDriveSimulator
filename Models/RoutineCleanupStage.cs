namespace DiscDriveSimulator.Models;

public class RoutineCleanupStage
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DiscSet Set1 { get; set; }
    public DiscSet Set2 { get; set; }

    public static readonly List<RoutineCleanupStage> AllStages = new()
    {
        new()
        {
            Id = "predator_prey",
            Name = "Predator & Prey",
            Description = "CRIT Rate & Ice DMG",
            Set1 = DiscSet.WoodpeckerElectro,
            Set2 = DiscSet.PolarMetal
        },
        new()
        {
            Id = "hunter_hound",
            Name = "Hunter & Hound",
            Description = "Impact & Electric DMG",
            Set1 = DiscSet.ShockstarDisco,
            Set2 = DiscSet.ThunderMetal
        },
        new()
        {
            Id = "mountain_blade",
            Name = "Mountain & Blade",
            Description = "Physical DMG & Anomaly Proficiency",
            Set1 = DiscSet.FangedMetal,
            Set2 = DiscSet.FreedomBlues
        },
        new()
        {
            Id = "puffer_chaos",
            Name = "Puffer & Chaos",
            Description = "PEN Ratio & Anomaly",
            Set1 = DiscSet.PufferElectro,
            Set2 = DiscSet.ChaosJazz
        },
        new()
        {
            Id = "rock_punk",
            Name = "Rock & Punk",
            Description = "ATK & DEF",
            Set1 = DiscSet.HormonePunk,
            Set2 = DiscSet.SoulRock
        },
        new()
        {
            Id = "jazz_song",
            Name = "Jazz & Song",
            Description = "Energy Regen & CRIT DMG",
            Set1 = DiscSet.SwingJazz,
            Set2 = DiscSet.BranchBladeSong
        }
    };
}
