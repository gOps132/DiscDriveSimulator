using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public interface IDiscGenerator
{
    Disc GenerateDisc(int? partition = null, DiscSet? set = null, bool isCrafted = false);
    List<Disc> GenerateRunDrops(DiscSet? set1 = null, DiscSet? set2 = null);
}
