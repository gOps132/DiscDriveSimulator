using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public class DiscGenerator : IDiscGenerator
{
    private readonly Random _random = new();

    public Disc GenerateDisc(int? partition = null, DiscSet? set = null, bool isCrafted = false)
    {
        int p = partition ?? _random.Next(1, 7);
        DiscSet s = set ?? (_random.Next(2) == 0 ? DiscSet.SetAlpha : DiscSet.SetBeta);

        StatType mainStat = PickMainStat(p);
        double mainStatValue = SimulationConfig.GetDefaultMainStatValue(p, mainStat);

        int initialSubCount = _random.Next(2) == 0 ? 3 : 4;

        // Substats: exclude main stat
        var pool = SimulationConfig.SubstatPool.Where(st => st != mainStat).ToList();
        
        // Fisher-Yates shuffle to pick distinct substats
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        var selectedStats = pool.Take(initialSubCount).ToList();
        var substats = new List<SubstatRoll>();

        foreach (var stat in selectedStats)
        {
            double val = SimulationConfig.SubstatRollValues.TryGetValue(stat, out double baseVal) ? baseVal : 0;
            substats.Add(new SubstatRoll(stat, val, 1));
        }

        return new Disc
        {
            Id = Guid.NewGuid(),
            Partition = p,
            Set = s,
            MainStat = mainStat,
            MainStatValue = mainStatValue,
            InitialSubstatCount = initialSubCount,
            Substats = substats,
            IsCrafted = isCrafted,
            CreatedAt = DateTime.UtcNow
        };
    }

    public List<Disc> GenerateRunDrops()
    {
        int dropCount = _random.Next(SimulationConfig.MinDropsPerRun, SimulationConfig.MaxDropsPerRun + 1);
        var drops = new List<Disc>(dropCount);
        for (int i = 0; i < dropCount; i++)
        {
            drops.Add(GenerateDisc());
        }
        return drops;
    }

    private StatType PickMainStat(int partition)
    {
        return partition switch
        {
            1 => StatType.HP,
            2 => StatType.ATK,
            3 => StatType.DEF,
            4 => SimulationConfig.Slot4MainStats[_random.Next(SimulationConfig.Slot4MainStats.Length)],
            5 => SimulationConfig.Slot5MainStats[_random.Next(SimulationConfig.Slot5MainStats.Length)],
            6 => SimulationConfig.Slot6MainStats[_random.Next(SimulationConfig.Slot6MainStats.Length)],
            _ => StatType.HP
        };
    }
}
