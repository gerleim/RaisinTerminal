using RaisinTerminal.Core.Collections;
using Xunit;

namespace RaisinTerminal.Tests;

public class CircularBufferTests
{
    [Fact]
    public void Add_BelowCapacity_CountIncreases()
    {
        var buf = new CircularBuffer<int>(5);
        buf.Add(1);
        buf.Add(2);
        Assert.Equal(2, buf.Count);
    }

    [Fact]
    public void Add_AtCapacity_CountStaysAtCapacity()
    {
        var buf = new CircularBuffer<int>(3);
        buf.Add(1);
        buf.Add(2);
        buf.Add(3);
        buf.Add(4); // evicts 1
        Assert.Equal(3, buf.Count);
    }

    [Fact]
    public void Add_ReturnsTrue_WhenEvicting()
    {
        var buf = new CircularBuffer<int>(2);
        Assert.False(buf.Add(1));
        Assert.False(buf.Add(2));
        Assert.True(buf.Add(3)); // evicts 1
    }

    [Fact]
    public void Indexer_BelowCapacity_ReturnsInOrder()
    {
        var buf = new CircularBuffer<int>(5);
        buf.Add(10);
        buf.Add(20);
        buf.Add(30);
        Assert.Equal(10, buf[0]);
        Assert.Equal(20, buf[1]);
        Assert.Equal(30, buf[2]);
    }

    [Fact]
    public void Indexer_AfterWraparound_ReturnsCorrectOrder()
    {
        var buf = new CircularBuffer<int>(3);
        buf.Add(1);
        buf.Add(2);
        buf.Add(3);
        buf.Add(4); // evicts 1
        buf.Add(5); // evicts 2

        // Should contain [3, 4, 5]
        Assert.Equal(3, buf[0]);
        Assert.Equal(4, buf[1]);
        Assert.Equal(5, buf[2]);
    }

    [Fact]
    public void Indexer_MultipleWraparounds_StillCorrect()
    {
        var buf = new CircularBuffer<int>(3);
        for (int i = 1; i <= 10; i++)
            buf.Add(i);

        // Should contain [8, 9, 10]
        Assert.Equal(3, buf.Count);
        Assert.Equal(8, buf[0]);
        Assert.Equal(9, buf[1]);
        Assert.Equal(10, buf[2]);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var buf = new CircularBuffer<int>(5);
        buf.Add(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => buf[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buf[-1]);
    }

    [Fact]
    public void Clear_ResetsCountToZero()
    {
        var buf = new CircularBuffer<int>(3);
        buf.Add(1);
        buf.Add(2);
        buf.Clear();
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void Capacity_IsCorrect()
    {
        var buf = new CircularBuffer<string>(100);
        Assert.Equal(100, buf.Capacity);
    }

    [Fact]
    public void Add_SingleCapacity_EvictsImmediately()
    {
        var buf = new CircularBuffer<int>(1);
        Assert.False(buf.Add(1));
        Assert.Equal(1, buf.Count);
        Assert.Equal(1, buf[0]);

        Assert.True(buf.Add(2));
        Assert.Equal(1, buf.Count);
        Assert.Equal(2, buf[0]);
    }
}
