using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public interface IDiscGenerator
{
    // A uniform double in [0, 1) from the generator's own RNG stream.
    // The simulation samples Roll Value this way rather than holding a private RNG, so disc
    // generation stays a pure function of run count: sampling one extra RV cannot shift
    // which substats a disc receives.
    double NextUniform();

    Disc GenerateDisc(int? partition = null, DiscSet? set = null, bool isCrafted = false);
    List<Disc> GenerateRunDrops(DiscSet? set1 = null, DiscSet? set2 = null);

    // Drop count for the next run, uniform over the configured range.
    int NextDropCount();

    // Draws a partition uniformly from 1-6, as GenerateDisc does.
    int NextPartition();

    // Set for one drop: one of set1/set2 at random when
    // both are supplied, else whichever was. Null when neither is, meaning "draw any set".
    DiscSet? NextDropSet(DiscSet? set1 = null, DiscSet? set2 = null);

    // Main stat for a partition: fixed on slots 1-3, uniform over the slot pool on 4-6.
    StatType NextMainStat(int partition);

    // Substat count on a fresh disc: 3 or 4 with equal probability.
    int NextInitialSubstatCount();

    // Builds a disc with forced partition, main stat, and Roll Value.
    // Lets the fast path commit to a sampled RV by drawing substats conditioned on it
    // rather than drawing and hoping. Still uniform over distinct substats, so the disc is
    // indistinguishable from one that happened to roll that way.
    // substatCount is supplied by the caller because it samples RV from a table keyed on this
    // count; drawing it here would decouple composition from the RV the disc was kept for.
    Disc GenerateDiscWithRollValue(int partition, DiscSet? set, StatType mainStat,
        int substatCount, int desiredRollValue, IReadOnlyCollection<StatType> prioritized);
}