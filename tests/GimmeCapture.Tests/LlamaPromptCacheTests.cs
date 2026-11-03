using System.Linq;
using GimmeCapture.Services.Translation;
using LLama.Native;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="LlamaPromptCache"/> decides how much of a prompt a kept context can skip reading: what it shares from the
/// start with the tokens the context holds, never the prompt's last token, whose logits the answer starts from.
/// </summary>
public sealed class LlamaPromptCacheTests
{
    [Fact]
    public void AnEmptyContext_KeepsNothing()
    {
        Assert.Equal(0, new LlamaPromptCache().Reusable(Tokens(1, 2, 3)));
    }

    [Fact]
    public void APromptThatBeginsAsTheHeldOne_KeepsWhatTheyShare()
    {
        // The instructions (1-4), then another text, after one translation and its answer (9, 9).
        var cache = Held(1, 2, 3, 4, 5, 6, 9, 9);

        Assert.Equal(4, cache.Reusable(Tokens(1, 2, 3, 4, 7, 8)));
    }

    [Fact]
    public void ThePromptHeldWhole_StillHasItsLastTokenRead()
    {
        Assert.Equal(3, Held(1, 2, 3, 4).Reusable(Tokens(1, 2, 3, 4)));
        Assert.Equal(3, Held(1, 2, 3, 4, 5, 6).Reusable(Tokens(1, 2, 3, 4)));
    }

    [Fact]
    public void APromptLongerThanWhatIsHeld_KeepsAllOfIt()
    {
        Assert.Equal(3, Held(1, 2, 3).Reusable(Tokens(1, 2, 3, 4, 5)));
    }

    [Fact]
    public void APromptThatPartsAtOnce_KeepsNothing()
    {
        Assert.Equal(0, Held(1, 2, 3).Reusable(Tokens(7, 2, 3)));
        Assert.Equal(0, Held(1, 2, 3).Reusable(Tokens(1)));
    }

    [Fact]
    public void Truncating_DropsWhatFollows_AndAppendingCountsOn()
    {
        var cache = Held(1, 2, 3, 4, 5);

        cache.Truncate(2);
        cache.Append(Tokens(8));
        cache.Append((LLamaToken)9);

        Assert.Equal(4, cache.Count);
        Assert.Equal(3, cache.Reusable(Tokens(1, 2, 8, 6)));
        Assert.Equal(4, cache.Reusable(Tokens(1, 2, 8, 9, 6)));
    }

    [Fact]
    public void Clearing_KeepsNothing()
    {
        var cache = Held(1, 2, 3);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Reusable(Tokens(1, 2, 3, 4)));
    }

    private static LlamaPromptCache Held(params int[] tokens)
    {
        var cache = new LlamaPromptCache();
        cache.Append(Tokens(tokens));
        return cache;
    }

    private static LLamaToken[] Tokens(params int[] tokens) => tokens.Select(t => (LLamaToken)t).ToArray();
}
