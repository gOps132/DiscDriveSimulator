using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public interface ISimulationService
{
    SimulationState State { get; }
    event Action? OnChange;

    void SetState(SimulationState state);
    SimulationRunResult RunSimulation(int batteryCharge);
    bool DismantleDisc(Disc disc);
    int DismantleDiscs(IEnumerable<Disc> discs);
    int DismantleNonMatching();
    Disc? CraftSingle(int partition);
    int CraftMultiple(int partition, int count);
    int CraftMax(int partition);
    bool MatchesFilter(Disc disc);
    List<Disc> GetFilteredDiscs();
    void Reset();
    void NotifyStateChanged();
}
