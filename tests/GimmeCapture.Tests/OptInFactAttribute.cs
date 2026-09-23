using System;
using System.Linq;

namespace GimmeCapture.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for an opt-in test that needs something only a developer's machine provides — a
/// real video clip, an output folder, a render-probe switch — named by environment variables. While any of them is
/// missing the test is reported as SKIPPED, naming what it wants. The early <c>return</c> this replaces made every
/// CI run count it as a pass for an encode or a render that never happened.
/// </summary>
/// <remarks>
/// Each requirement is either <c>NAME</c> (set to anything non-blank) or <c>NAME=value</c> (set to exactly that).
/// </remarks>
public sealed class OptInFactAttribute : FactAttribute
{
    public OptInFactAttribute(params string[] requiredEnvironment)
    {
        string[] missing = requiredEnvironment.Where(requirement => !IsMet(requirement)).ToArray();
        if (missing.Length > 0)
        {
            Skip = $"Opt-in test: set {string.Join(" and ", missing)} to run it.";
        }
    }

    private static bool IsMet(string requirement)
    {
        int separator = requirement.IndexOf('=');
        if (separator < 0)
        {
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(requirement));
        }

        return Environment.GetEnvironmentVariable(requirement[..separator]) == requirement[(separator + 1)..];
    }
}
