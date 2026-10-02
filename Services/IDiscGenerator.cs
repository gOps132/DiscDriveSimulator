using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public interface IDiscGenerator
{
    Disc GenerateDisc(int? partition = null, DiscSet? set = null, bool isCrafted = false);
    List<Disc> GenerateRunDrops(DiscSet? set1 = null, DiscSet? set2 = null);

    /// <summary>
    /// How many discs the next run drops, without generating them.
    /// </summary>
    /// <remarks>
    /// Lets the simulation fast path count drops for every disc while only building the
    /// ones it keeps. Draws from the same distribution as
    /// <see cref="GenerateRunDrops"/>: uniform over
    /// <c>[MinDropsPerRun, MaxDropsPerRun]</c>.
    /// </remarks>
    int NextDropCount();

    /// <summary>Draws a partition uniformly from 1-6, as <see cref="GenerateDisc"/> does.</summary>
    int NextPartition();

    /// <summary>
    /// Draws the set for one drop: one of <paramref name="set1"/>/<paramref name="set2"/>
    /// at random when both are supplied, otherwise whichever single set was configured.
    /// Returns <c>null</c> when neither is supplied, meaning "draw randomly from all sets".
    /// </summary>
    DiscSet? NextDropSet(DiscSet? set1 = null, DiscSet? set2 = null);

    /// <summary>
    /// Draws the main stat for a given partition, matching the distribution
    /// <see cref="GenerateDisc"/> uses: fixed on slots 1-3, uniform over the slot pool on 4-6.
    /// </summary>
    StatType NextMainStat(int partition);

    /// <summary>
    /// Draws the number of substats on a fresh disc: 3 or 4 with equal probability.
    /// </summary>
    int NextInitialSubstatCount();

    /// <summary>
    /// Builds a disc with a forced partition, main stat, and Roll Value.
    /// </summary>
    /// <remarks>
    /// This is what lets the simulation fast path commit to a sampled RV: rather than draw
    /// substats and hope they match, it draws substats <i>conditioned</i> on the target RV.
    /// The marginal distribution of the resulting disc is identical to
    /// <see cref="GenerateDisc"/> — it is still uniform over distinct substats — because
    /// choosing which <c>rv</c> of the <c>k</c> slots hold prioritized stats is itself
    /// uniform over the <c>C(k, rv)</c> valid positions.
    /// </remarks>
    /// <param name="partition">Partition 1-6.</param>
    /// <param name="set">Disc set, or null to draw one.</param>
    /// <param name="mainStat">Main stat to force.</param>
    /// <param name="desiredRollValue">How many of the drawn substats must be prioritized.</param>
    /// <param name="prioritized">The prioritized substats for this partition.</param>
    Disc GenerateDiscWithRollValue(int partition, DiscSet? set, StatType mainStat,
        int desiredRollValue, IReadOnlyCollection<StatType> prioritized);
}