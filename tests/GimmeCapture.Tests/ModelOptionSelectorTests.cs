using System.Collections.Generic;

namespace GimmeCapture.Tests;

public class ModelOptionSelectorTests
{
    private sealed record Opt(string Id);

    private static IReadOnlyList<Opt> Models() =>
    [
        new Opt("a"),
        new Opt("b"),
        new Opt("c")
    ];

    [Fact]
    public void FindIndexById_NullOption_ReturnsMinusOne()
    {
        Assert.Equal(-1, ModelOptionSelector.FindIndexById<Opt>(null, Models(), m => m.Id));
    }

    [Fact]
    public void FindIndexById_MatchById_ReturnsIndex()
    {
        // A different instance with the same id matches by ordinal id equality.
        Assert.Equal(1, ModelOptionSelector.FindIndexById(new Opt("b"), Models(), m => m.Id));
    }

    [Fact]
    public void FindIndexById_SameReference_ReturnsIndex()
    {
        var models = Models();
        Assert.Equal(2, ModelOptionSelector.FindIndexById(models[2], models, m => m.Id));
    }

    [Fact]
    public void FindIndexById_NotFound_ReturnsMinusOne()
    {
        Assert.Equal(-1, ModelOptionSelector.FindIndexById(new Opt("z"), Models(), m => m.Id));
    }

    [Fact]
    public void FindIndexById_DuplicateIds_ReturnsFirstMatch()
    {
        IReadOnlyList<Opt> models = [new Opt("dup"), new Opt("dup")];
        Assert.Equal(0, ModelOptionSelector.FindIndexById(new Opt("dup"), models, m => m.Id));
    }
}
