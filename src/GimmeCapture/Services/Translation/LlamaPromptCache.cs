using System;
using System.Collections.Generic;
using LLama.Native;

namespace GimmeCapture.Services.Translation;

/// <summary>
/// The tokens a kept context holds for its one sequence, at positions 0 to <see cref="Count"/> − 1, and how many of a
/// new prompt's it can keep: those the prompt shares with it from the start. The prompt's last token is always decoded
/// again, as the answer's first token is sampled from its logits.
/// </summary>
internal sealed class LlamaPromptCache
{
    private readonly List<int> _held = [];

    public int Count => _held.Count;

    /// <summary>How many of <paramref name="prompt"/>'s first tokens are held already, short of its last.</summary>
    public int Reusable(ReadOnlySpan<LLamaToken> prompt)
    {
        int limit = Math.Min(_held.Count, prompt.Length - 1);
        int shared = 0;
        while (shared < limit && _held[shared] == (int)prompt[shared])
        {
            shared++;
        }

        return shared;
    }

    /// <summary>The context dropped every token from position <paramref name="count"/> on.</summary>
    public void Truncate(int count) => _held.RemoveRange(count, _held.Count - count);

    /// <summary>The context decoded <paramref name="tokens"/> after those it held.</summary>
    public void Append(ReadOnlySpan<LLamaToken> tokens)
    {
        foreach (LLamaToken token in tokens)
        {
            _held.Add((int)token);
        }
    }

    public void Append(LLamaToken token) => _held.Add((int)token);

    public void Clear() => _held.Clear();
}
