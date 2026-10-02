using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public interface ISimulationService
{
    SimulationState State { get; }
    event Action? OnChange;

    void SetState(SimulationState state);
    SimulationRunResult RunSimulation(int batteryCharge);
    Task<SimulationRunResult> RunSimulationAsync(int batteryCharge, IProgress<double>? progress = null, CancellationToken ct = default);
    bool DismantleDisc(Disc disc);
    int DismantleDiscs(IEnumerable<Disc> discs);
    int DismantleNonMatching();
    Disc? CraftSingle(int? partition = null, DiscSet? set = null);
    int CraftMultiple(int? partition, int count, DiscSet? set = null);
    int CraftMax(int? partition, DiscSet? set = null);
    bool MatchesFilter(Disc disc);
    List<Disc> GetFilteredDiscs();

    /// <summary>
    /// Filtered, sorted, and tallied inventory in a single pass.
    /// </summary>
    /// <remarks>
    /// Prefer this over <see cref="GetFilteredDiscs"/> for rendering: it avoids the repeated
    /// filter-and-sort passes a full inventory would otherwise pay on every render.
    /// </remarks>
    StorageView GetStorageView();
    void Reset();
    void NotifyStateChanged();
}
