namespace DiscDriveSimulator.Components;

// Paging math shared by the storage and obtain grids. Both render DiscsPerPage cards per page
// over an already filtered and sorted list, so the window arithmetic lives here once instead
// of drifting apart in two modals.
public static class PagerState
{
    // Cards rendered per page. Keeps a full grid light enough to stay interactive.
    public const int DiscsPerPage = 100;

    // How many pages a list needs. Always at least 1 so the pager has something to show.
    public static int TotalPages(int count, int perPage = DiscsPerPage) =>
        Math.Max(1, (int)Math.Ceiling(count / (double)perPage));

    // Pulls an out-of-range page back inside 1..totalPages. A shrunk list (dismantles, tighter
    // filters) must never strand the grid past its end.
    public static int ClampPage(int page, int totalPages) =>
        Math.Clamp(page, 1, Math.Max(1, totalPages));

    // (skip, take) window for a 1-based page. Overflow pages return the last page's window.
    public static (int Skip, int Take) PageWindow(int count, int page, int perPage = DiscsPerPage)
    {
        if (count <= 0) return (0, 0);
        int clamped = ClampPage(page, TotalPages(count, perPage));
        int skip = (clamped - 1) * perPage;
        return (skip, Math.Min(perPage, count - skip));
    }

    // 1-based inclusive (start, end) for the "showing A-B of N" label. (0, 0) when empty.
    public static (int Start, int End) RangeLabel(int count, int page, int perPage = DiscsPerPage)
    {
        var (skip, take) = PageWindow(count, page, perPage);
        return take == 0 ? (0, 0) : (skip + 1, skip + take);
    }

    // Which 1-based page holds a 0-based index. Used to jump to the inspected disc on open.
    public static int PageForIndex(int index, int perPage = DiscsPerPage) =>
        (Math.Max(0, index) / perPage) + 1;
}