using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public class DiscGenerator : IDiscGenerator
{
    private readonly Random _random;
    private static readonly DiscSet[] AllSets = Enum.GetValues<DiscSet>()
        .Where(s => s != DiscSet.SetAlpha && s != DiscSet.SetBeta)
        .ToArray();

    public DiscGenerator() : this(new Random())
    {
    }

    /// <summary>
    /// Test seam: allows a seeded <see cref="Random"/> so distribution tests can compare
    /// sampled RV against real generated discs deterministically.
    /// </summary>
    public DiscGenerator(Random random)
    {
        _random = random;
    }

    public Disc GenerateDisc(int? partition = null, DiscSet? set = null, bool isCrafted = false)
    {
        int p = partition ?? NextPartition();
        DiscSet s = set ?? AllSets[_random.Next(AllSets.Length)];

        StatType mainStat = NextMainStat(p);
        double mainStatValue = SimulationConfig.GetDefaultMainStatValue(p, mainStat);

        int initialSubCount = NextInitialSubstatCount();

        return new Disc
        {
            Id = Guid.NewGuid(),
            Partition = p,
            Set = s,
            MainStat = mainStat,
            MainStatValue = mainStatValue,
            InitialSubstatCount = initialSubCount,
            Substats = GenerateSubstats(mainStat, initialSubCount),
            IsCrafted = isCrafted,
            CreatedAt = DateTime.UtcNow
        };
    }

    public List<Disc> GenerateRunDrops(DiscSet? set1 = null, DiscSet? set2 = null)
    {
        int dropCount = NextDropCount();
        var drops = new List<Disc>(dropCount);
        for (int i = 0; i < dropCount; i++)
        {
            drops.Add(GenerateDisc(set: NextDropSet(set1, set2)));
        }
        return drops;
    }

    public int NextDropCount()
    {
        return _random.Next(SimulationConfig.MinDropsPerRun, SimulationConfig.MaxDropsPerRun + 1);
    }

    public int NextPartition() => _random.Next(1, 7);

    public DiscSet? NextDropSet(DiscSet? set1 = null, DiscSet? set2 = null)
    {
        if (set1.HasValue && set2.HasValue)
        {
            return _random.Next(2) == 0 ? set1.Value : set2.Value;
        }
        return set1 ?? set2;
    }

    public StatType NextMainStat(int partition)
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

    public int NextInitialSubstatCount() => _random.Next(2) == 0 ? 3 : 4;

    public Disc GenerateDiscWithRollValue(int partition, DiscSet? set, StatType mainStat,
        int substatCount, int desiredRollValue, IReadOnlyCollection<StatType> prioritized)
    {
        DiscSet s = set ?? AllSets[_random.Next(AllSets.Length)];

        return new Disc
        {
            Id = Guid.NewGuid(),
            Partition = partition,
            Set = s,
            MainStat = mainStat,
            MainStatValue = SimulationConfig.GetDefaultMainStatValue(partition, mainStat),
            InitialSubstatCount = substatCount,
            Substats = GenerateSubstatsWithRollValue(mainStat, substatCount, desiredRollValue, prioritized),
            IsCrafted = false,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Draws <paramref name="count"/> distinct substats uniformly from the pool minus the
    /// main stat, via Fisher-Yates on a fresh list.
    /// </summary>
    private List<SubstatRoll> GenerateSubstats(StatType mainStat, int count)
    {
        var pool = BuildPool(mainStat);
        Shuffle(pool);

        var substats = new List<SubstatRoll>(count);
        for (int i = 0; i < count; i++)
        {
            substats.Add(MakeRoll(pool[i]));
        }

        return substats;
    }

    /// <summary>
    /// Draws substats conditioned on exactly <paramref name="desiredRollValue"/> of them
    /// being prioritized.
    /// </summary>
    /// <remarks>
    /// Splits the pool into prioritized and non-prioritized groups, takes
    /// <paramref name="desiredRollValue"/> distinct prioritized stats and the remainder from
    /// the rest, then interleaves them in uniformly random order.
    /// <para>
    /// The result is uniform over every substat subset with that exact RV, which is what
    /// makes this safe to use: conditioning on RV and then drawing uniformly within the
    /// conditioned set leaves the marginal distribution identical to ordinary generation.
    /// A disc built this way is indistinguishable from one that happened to roll that way.
    /// </para>
    /// </remarks>
    private List<SubstatRoll> GenerateSubstatsWithRollValue(StatType mainStat, int count,
        int desiredRollValue, IReadOnlyCollection<StatType> prioritized)
    {
        var wanted = new List<StatType>(count);
        var other = new List<StatType>(SimulationConfig.SubstatPool.Length);

        foreach (var stat in BuildPool(mainStat))
        {
            if (prioritized.Contains(stat)) wanted.Add(stat);
            else other.Add(stat);
        }

        if (desiredRollValue < 0 || desiredRollValue > count ||
            desiredRollValue > wanted.Count || count - desiredRollValue > other.Count)
        {
            // Unreachable while the caller's RV came from a table built over this same pool,
            // but silently emitting a disc with the wrong RV would corrupt every statistic.
            // The `> count` guard matters when more than 4 stats are prioritized: the
            // substat mixture tops out at min(d, 4), so a 3-substat disc can be asked for
            // an RV of 4, which passes the availability checks but would emit 4 substats
            // while reporting InitialSubstatCount = 3.
            throw new InvalidOperationException(
                $"Cannot build {count} substats with RV {desiredRollValue} for main stat " +
                $"{mainStat}: {wanted.Count} prioritized and {other.Count} other available.");
        }

        Shuffle(wanted);
        Shuffle(other);

        var chosen = new List<StatType>(count);
        for (int i = 0; i < desiredRollValue; i++) chosen.Add(wanted[i]);
        for (int i = 0; i < count - desiredRollValue; i++) chosen.Add(other[i]);
        Shuffle(chosen);

        var substats = new List<SubstatRoll>(count);
        foreach (var stat in chosen) substats.Add(MakeRoll(stat));

        return substats;
    }

    /// <summary>Substat pool members other than the main stat.</summary>
    private static List<StatType> BuildPool(StatType mainStat)
    {
        var pool = new List<StatType>(SimulationConfig.SubstatPool.Length - 1);
        foreach (var stat in SimulationConfig.SubstatPool)
        {
            if (stat != mainStat) pool.Add(stat);
        }
        return pool;
    }

    private void Shuffle(List<StatType> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static SubstatRoll MakeRoll(StatType stat)
    {
        double val = SimulationConfig.SubstatRollValues.TryGetValue(stat, out double baseVal) ? baseVal : 0;
        return new SubstatRoll(stat, val, 1);
    }
}
