using DiscDriveSimulator.Components;

namespace DiscDriveSimulator.Tests;

/// <summary>
/// Paging math shared by the storage and obtain grids. Both render 100 cards per page over an
/// already filtered and sorted list.
/// </summary>
public class PagerStateTests
{
    [Fact]
    public void TotalPages_RoundsUpPartialPages()
    {
        Assert.Equal(1, PagerState.TotalPages(0, 100));
        Assert.Equal(1, PagerState.TotalPages(1, 100));
        Assert.Equal(1, PagerState.TotalPages(100, 100));
        Assert.Equal(2, PagerState.TotalPages(101, 100));
        Assert.Equal(50, PagerState.TotalPages(5000, 100));
    }

    [Fact]
    public void ClampPage_KeepsValidPagesAndPullsBackOverflow()
    {
        Assert.Equal(1, PagerState.ClampPage(1, 5));
        Assert.Equal(5, PagerState.ClampPage(5, 5));
        Assert.Equal(5, PagerState.ClampPage(9, 5));
        Assert.Equal(1, PagerState.ClampPage(0, 5));
        Assert.Equal(1, PagerState.ClampPage(-3, 5));
        Assert.Equal(1, PagerState.ClampPage(7, 1));
    }

    [Fact]
    public void PageWindow_CoversExactlyOnePage()
    {
        Assert.Equal((0, 100), PagerState.PageWindow(250, 1));
        Assert.Equal((100, 100), PagerState.PageWindow(250, 2));
        Assert.Equal((200, 50), PagerState.PageWindow(250, 3));
    }

    [Fact]
    public void PageWindow_EmptyListYieldsEmptyWindow()
    {
        Assert.Equal((0, 0), PagerState.PageWindow(0, 1));
    }

    [Fact]
    public void PageWindow_ClampsOverflowPages()
    {
        // Asking past the end returns the last page's window, never an empty one.
        Assert.Equal((200, 50), PagerState.PageWindow(250, 9));
    }

    [Fact]
    public void RangeLabel_IsOneBasedAndInclusive()
    {
        Assert.Equal((0, 0), PagerState.RangeLabel(0, 1));
        Assert.Equal((1, 100), PagerState.RangeLabel(250, 1));
        Assert.Equal((101, 200), PagerState.RangeLabel(250, 2));
        Assert.Equal((201, 250), PagerState.RangeLabel(250, 3));
    }

    [Fact]
    public void PageForIndex_FindsThePageHoldingAnItem()
    {
        Assert.Equal(1, PagerState.PageForIndex(0, 100));
        Assert.Equal(1, PagerState.PageForIndex(99, 100));
        Assert.Equal(2, PagerState.PageForIndex(100, 100));
        Assert.Equal(3, PagerState.PageForIndex(250, 100));
    }
}