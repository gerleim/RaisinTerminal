using RaisinTerminal.Core.Helpers;
using Xunit;

namespace RaisinTerminal.Tests;

public class ProjectNameHelperTests
{
    [Theory]
    [InlineData("RaisinTerminal", "RT")]
    [InlineData("StockRaisin2", "SR2")]
    [InlineData("MyApp", "MA")]
    [InlineData("HelloWorld", "HW")]
    public void Abbreviate_CamelCase_ExtractsCapitalsAndTrailingDigits(string name, string expected)
    {
        Assert.Equal(expected, ProjectNameHelper.Abbreviate(name));
    }

    [Theory]
    [InlineData("SNOP DEV PBIP CLAUDE", "SNOP")]
    [InlineData("SNOP DEV PBIP IL CLAUDE", "SNOP")]
    [InlineData("MY PROJECT", "MY")]
    public void Abbreviate_AllCapsMultiWord_ReturnsFirstWord(string name, string expected)
    {
        Assert.Equal(expected, ProjectNameHelper.Abbreviate(name));
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("ABC", "ABC")]
    [InlineData("A", "A")]
    public void Abbreviate_Fallback_ReturnsAsIs(string name, string expected)
    {
        Assert.Equal(expected, ProjectNameHelper.Abbreviate(name));
    }

    [Fact]
    public void AbbreviateWithDisambiguation_NoCollision_ReturnsPlainAbbreviation()
    {
        var allNames = new[] { "RaisinTerminal", "StockRaisin2" };
        Assert.Equal("RT", ProjectNameHelper.AbbreviateWithDisambiguation("RaisinTerminal", allNames));
        Assert.Equal("SR2", ProjectNameHelper.AbbreviateWithDisambiguation("StockRaisin2", allNames));
    }

    [Fact]
    public void AbbreviateWithDisambiguation_Collision_AppendsSortedIndex()
    {
        var allNames = new[] { "SNOP DEV PBIP CLAUDE", "SNOP DEV PBIP IL CLAUDE" };

        // Both abbreviate to "SNOP"; sorted alphabetically:
        // "SNOP DEV PBIP CLAUDE" < "SNOP DEV PBIP IL CLAUDE"
        Assert.Equal("SNOP1", ProjectNameHelper.AbbreviateWithDisambiguation("SNOP DEV PBIP CLAUDE", allNames));
        Assert.Equal("SNOP2", ProjectNameHelper.AbbreviateWithDisambiguation("SNOP DEV PBIP IL CLAUDE", allNames));
    }

    [Fact]
    public void AbbreviateWithDisambiguation_SingleProject_NoSuffix()
    {
        var allNames = new[] { "SNOP DEV PBIP CLAUDE" };
        Assert.Equal("SNOP", ProjectNameHelper.AbbreviateWithDisambiguation("SNOP DEV PBIP CLAUDE", allNames));
    }

    [Theory]
    [InlineData("RT", 1, "RT 1")]
    [InlineData("SNOP1", 3, "SNOP1 3")]
    [InlineData("S", 1, "S 1")]
    public void GenerateSessionName_FormatsCorrectly(string abbreviation, int number, string expected)
    {
        Assert.Equal(expected, ProjectNameHelper.GenerateSessionName(abbreviation, number));
    }
}
