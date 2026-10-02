using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public interface IDiscGenerator
{
    /// <summary>
    /// A uniform double in <c>[0, 1)</c> from the generator's own RNG stream.
    /// </summary>
    /// <remarks>
    /// The simulation samples Roll Value this way rather than holding a private RNG, so disc
    /// generation stays a pure function of run count: sampling one extra RV cannot shift
    /// which substats a disc receives.
    /// </remarks>
    double NextUniform();

    Disc GenerateDisc(int? partition = null, DiscSet? set = null, bool isCrafted = false);
    List<Disc> GenerateRunDrops(DiscSet? set1 = null, DiscSet? set2 = null);

    /// <summary>Drop count for the next run, uniform over the configured range.</summary>
    int NextDropCount();

    /// <summary>Draws a partition uniformly from 1-6, as <see cref="GenerateDisc"/> does.</summary>
    int NextPartition();

    /// <summary>
    /// Set for one drop: one of <paramref name="set1"/>/<paramref name="set2"/> at random when
    /// both are supplied, else whichever was. Null when neither is, meaning "draw any set".
    /// </summary>
    DiscSet? NextDropSet(DiscSet? set1 = null, DiscSet? set2 = null);

    /// <summary>Main stat for a partition: fixed on slots 1-3, uniform over the slot pool on 4-6.</summary>
    StatType NextMainStat(int partition);

    /// <summary>Substat count on a fresh disc: 3 or 4 with equal probability.</summary>
    int NextInitialSubstatCount();

    /// <summary>
    /// Builds a disc with forced partition, main stat, and Roll Value.
    /// </summary>
    /// <remarks>
    /// Lets the fast path commit to a sampled RV by drawing substats <i>conditioned</i> on it
    /// rather than drawing and hoping. Still uniform over distinct substats, so the disc is
    /// indistinguishable from one that happened to roll that way.
    /// </remarks>
    /// <param name="substatCount">
    /// Supplied by the caller because it samples RV from a table keyed on this count; drawing
    /// it here would decouple composition from the RV the disc was kept for.
    /// </param>
    Disc GenerateDiscWithRollValue(int partition, DiscSet? set, StatType mainStat,
        int substatCount, int desiredRollValue, IReadOnlyCollection<StatType> prioritized);
}