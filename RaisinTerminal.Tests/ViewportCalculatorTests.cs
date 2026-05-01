using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class ViewportCalculatorTests
{
    [Fact]
    public void BaseRowCount_BufferSmallerThanCanvas()
    {
        Assert.Equal(10, ViewportCalculator.BaseRowCount(10, 30));
    }

    [Fact]
    public void BaseRowCount_BufferEqualsCanvas()
    {
        Assert.Equal(30, ViewportCalculator.BaseRowCount(30, 30));
    }

    [Fact]
    public void BaseRowCount_BufferLargerThanCanvas()
    {
        Assert.Equal(30, ViewportCalculator.BaseRowCount(50, 30));
    }

    [Fact]
    public void ViewOffset_BufferFitsCanvas()
    {
        Assert.Equal(0, ViewportCalculator.ViewOffset(10, 30));
    }

    [Fact]
    public void ViewOffset_BufferEqualsCanvas()
    {
        Assert.Equal(0, ViewportCalculator.ViewOffset(30, 30));
    }

    [Fact]
    public void ViewOffset_BufferExceedsCanvas()
    {
        Assert.Equal(20, ViewportCalculator.ViewOffset(50, 30));
    }

    [Fact]
    public void MaxScrollOffset_NoScrollback_BufferFits()
    {
        Assert.Equal(0, ViewportCalculator.MaxScrollOffset(30, 30, 0));
    }

    [Fact]
    public void MaxScrollOffset_NoScrollback_BufferLarger()
    {
        Assert.Equal(20, ViewportCalculator.MaxScrollOffset(50, 30, 0));
    }

    [Fact]
    public void MaxScrollOffset_WithScrollback_BufferFits()
    {
        Assert.Equal(100, ViewportCalculator.MaxScrollOffset(30, 30, 100));
    }

    [Fact]
    public void MaxScrollOffset_WithScrollback_BufferLarger()
    {
        Assert.Equal(120, ViewportCalculator.MaxScrollOffset(50, 30, 100));
    }

    [Fact]
    public void MaxScrollOffset_CanvasBiggerThanBuffer()
    {
        Assert.Equal(30, ViewportCalculator.MaxScrollOffset(10, 30, 50));
    }

    [Fact]
    public void DisplayCursorRow_NoOffset()
    {
        Assert.Equal(5, ViewportCalculator.DisplayCursorRow(5, 0));
    }

    [Fact]
    public void DisplayCursorRow_WithOffset()
    {
        Assert.Equal(5, ViewportCalculator.DisplayCursorRow(25, 20));
    }

    [Fact]
    public void ExtraRows_InsufficientPixels_ReturnsZero()
    {
        Assert.Equal(0, ViewportCalculator.ExtraRowsFromCompression(
            savedPixels: 15.0, cellHeight: 16.0, emptyRowScale: 0.25,
            scrollbackCount: 100, viewOffset: 0, scrollOffset: 0));
    }

    [Fact]
    public void ExtraRows_ExactlyCellHeight_ReturnsZero()
    {
        Assert.Equal(0, ViewportCalculator.ExtraRowsFromCompression(
            savedPixels: 16.0, cellHeight: 16.0, emptyRowScale: 0.25,
            scrollbackCount: 100, viewOffset: 0, scrollOffset: 0));
    }

    [Fact]
    public void ExtraRows_EnoughPixels_ReturnsComputed()
    {
        // savedPixels=32, cellHeight=16, emptyRowScale=0.25
        // compressedHeight = Round(16*0.25) = 4
        // maxExtra = Ceiling(32/4) = 8
        // availableAbove = 100 + 0 - 0 = 100
        // result = Min(8, 100) = 8
        Assert.Equal(8, ViewportCalculator.ExtraRowsFromCompression(
            savedPixels: 32.0, cellHeight: 16.0, emptyRowScale: 0.25,
            scrollbackCount: 100, viewOffset: 0, scrollOffset: 0));
    }

    [Fact]
    public void ExtraRows_ClampedByAvailableScrollback()
    {
        // savedPixels=100, cellHeight=16, emptyRowScale=0.25
        // compressedHeight=4, maxExtra=Ceiling(100/4)=25
        // availableAbove = 3 + 0 - 0 = 3
        // result = Min(25, 3) = 3
        Assert.Equal(3, ViewportCalculator.ExtraRowsFromCompression(
            savedPixels: 100.0, cellHeight: 16.0, emptyRowScale: 0.25,
            scrollbackCount: 3, viewOffset: 0, scrollOffset: 0));
    }

    [Fact]
    public void ExtraRows_ScrolledToMax_ReturnsZero()
    {
        // availableAbove = 10 + 0 - 10 = 0
        Assert.Equal(0, ViewportCalculator.ExtraRowsFromCompression(
            savedPixels: 100.0, cellHeight: 16.0, emptyRowScale: 0.25,
            scrollbackCount: 10, viewOffset: 0, scrollOffset: 10));
    }

    [Fact]
    public void ExtraRows_WithViewOffset()
    {
        // availableAbove = 5 + 10 - 0 = 15
        // maxExtra = Ceiling(32/4) = 8
        // result = Min(8, 15) = 8
        Assert.Equal(8, ViewportCalculator.ExtraRowsFromCompression(
            savedPixels: 32.0, cellHeight: 16.0, emptyRowScale: 0.25,
            scrollbackCount: 5, viewOffset: 10, scrollOffset: 0));
    }

    [Fact]
    public void AbsoluteRowBase_NoScrollNoExtras()
    {
        Assert.Equal(1000L, ViewportCalculator.AbsoluteRowBase(1000, 0, 0, 0));
    }

    [Fact]
    public void AbsoluteRowBase_WithViewOffset()
    {
        Assert.Equal(1020L, ViewportCalculator.AbsoluteRowBase(1000, 20, 0, 0));
    }

    [Fact]
    public void AbsoluteRowBase_WithScrollOffset()
    {
        Assert.Equal(900L, ViewportCalculator.AbsoluteRowBase(1000, 0, 100, 0));
    }

    [Fact]
    public void AbsoluteRowBase_WithExtraRows()
    {
        Assert.Equal(995L, ViewportCalculator.AbsoluteRowBase(1000, 0, 0, 5));
    }

    [Fact]
    public void AbsoluteRowBase_AllParameters()
    {
        // 1000 + 20 - 100 - 5 = 915
        Assert.Equal(915L, ViewportCalculator.AbsoluteRowBase(1000, 20, 100, 5));
    }

    [Fact]
    public void PinnedInitialOffset_BufferMatchesCanvas_NoScrollback()
    {
        // bufferRows == pinnedCanvasRows, no scrollback → offset = 0
        Assert.Equal(0, ViewportCalculator.PinnedInitialOffset(15, 15, 0));
    }

    [Fact]
    public void PinnedInitialOffset_BufferMatchesCanvas_WithScrollback()
    {
        // bufferRows == pinnedCanvasRows, viewOffset = 0 → offset = min(scrollback, canvas)
        Assert.Equal(15, ViewportCalculator.PinnedInitialOffset(15, 15, 100));
    }

    [Fact]
    public void PinnedInitialOffset_BufferLarger_NoScrollback()
    {
        // bufferRows=50, pinnedCanvas=15, scrollback=0
        // viewOffset = 35, maxOffset = 35
        // offset = min(35, 35 + 15) = 35
        Assert.Equal(35, ViewportCalculator.PinnedInitialOffset(50, 15, 0));
    }

    [Fact]
    public void PinnedInitialOffset_BufferLarger_WithScrollback()
    {
        // bufferRows=50, pinnedCanvas=15, scrollback=100
        // viewOffset = 35, maxOffset = 135
        // offset = min(135, 35 + 15) = 50
        Assert.Equal(50, ViewportCalculator.PinnedInitialOffset(50, 15, 100));
    }

    [Fact]
    public void PinnedInitialOffset_BufferSmaller()
    {
        // bufferRows=10, pinnedCanvas=15, scrollback=50
        // viewOffset = 0, maxOffset = 50
        // offset = min(50, 0 + 15) = 15
        Assert.Equal(15, ViewportCalculator.PinnedInitialOffset(10, 15, 50));
    }

    [Fact]
    public void PinnedInitialOffset_ClampedByMaxOffset()
    {
        // bufferRows=15, pinnedCanvas=15, scrollback=5
        // viewOffset = 0, maxOffset = 5
        // offset = min(5, 0 + 15) = 5
        Assert.Equal(5, ViewportCalculator.PinnedInitialOffset(15, 15, 5));
    }
}
