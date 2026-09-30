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
            Id = "stage_1",
            Name = "Monster and Weirdo",
            Description = "Swing Jazz & Chaotic Metal",
            Set1 = DiscSet.SwingJazz,
            Set2 = DiscSet.ChaoticMetal
        },
        new()
        {
            Id = "stage_2",
            Name = "Fist and Gun",
            Description = "Hormone Punk & Fanged Metal",
            Set1 = DiscSet.HormonePunk,
            Set2 = DiscSet.FangedMetal
        },
        new()
        {
            Id = "stage_3",
            Name = "Hunter and Hound",
            Description = "Shockstar Disco & Thunder Metal",
            Set1 = DiscSet.ShockstarDisco,
            Set2 = DiscSet.ThunderMetal
        },
        new()
        {
            Id = "stage_4",
            Name = "Tower and Cannon",
            Description = "Woodpecker Electro & Soul Rock",
            Set1 = DiscSet.WoodpeckerElectro,
            Set2 = DiscSet.SoulRock
        },
        new()
        {
            Id = "stage_5",
            Name = "Madman and Follower",
            Description = "Puffer Electro & Inferno Metal",
            Set1 = DiscSet.PufferElectro,
            Set2 = DiscSet.InfernoMetal
        },
        new()
        {
            Id = "stage_6",
            Name = "Sharp Fang and Blunt Axe",
            Description = "Freedom Blues & Polar Metal",
            Set1 = DiscSet.FreedomBlues,
            Set2 = DiscSet.PolarMetal
        },
        new()
        {
            Id = "stage_7",
            Name = "Hunter and Beast",
            Description = "Chaos Jazz & Proto Punk",
            Set1 = DiscSet.ChaosJazz,
            Set2 = DiscSet.ProtoPunk
        },
        new()
        {
            Id = "stage_8",
            Name = "Monster Duet",
            Description = "Branch and Blade Song & Astral Voice",
            Set1 = DiscSet.BranchBladeSong,
            Set2 = DiscSet.AstralVoice
        },
        new()
        {
            Id = "stage_9",
            Name = "Gunslinger vs Guardian",
            Description = "Phaethon's Melody & Shadow Harmony",
            Set1 = DiscSet.PhaethonsMelody,
            Set2 = DiscSet.ShadowHarmony
        },
        new()
        {
            Id = "stage_10",
            Name = "Words and Weapons",
            Description = "Yunkui Tales & King of the Summit",
            Set1 = DiscSet.YunkuiTales,
            Set2 = DiscSet.KingOfTheSummit
        },
        new()
        {
            Id = "stage_11",
            Name = "Iron Law and Outlaws",
            Description = "Dawn's Bloom & Moonlight Lullaby",
            Set1 = DiscSet.DawnsBloom,
            Set2 = DiscSet.MoonlightLullaby
        },
        new()
        {
            Id = "stage_12",
            Name = "Deceits and Bulwarks",
            Description = "White Water Ballad & Shining Aria",
            Set1 = DiscSet.WhiteWaterBallad,
            Set2 = DiscSet.ShiningAria
        },
        new()
        {
            Id = "stage_13",
            Name = "Dragon Taming Chariot",
            Description = "Bunny in Wonderland & Notes From the Chained",
            Set1 = DiscSet.BunnyInWonderland,
            Set2 = DiscSet.NotesFromTheChained
        },
        new()
        {
            Id = "stage_14",
            Name = "Pilot and the Rebel Mech",
            Description = "The Sky Ablaze & Wuthering Salon",
            Set1 = DiscSet.TheSkyAblaze,
            Set2 = DiscSet.WutheringSalon
        },
        new()
        {
            Id = "stage_15",
            Name = "Swiftspine and Splitclaw",
            Description = "Feathered Fate & Thorned Rose",
            Set1 = DiscSet.FeatheredFate,
            Set2 = DiscSet.ThornedRose
        }
    };
}
