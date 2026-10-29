namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="GimmeCapture.Services.Core.AI.AIResourceService.RequestGlobalUnload"/> is static: a test that removes a
/// module or turns AI off unloads every runtime's sessions in the process, another test's included. The tests that
/// raise it and the tests that need their sessions to stay loaded take turns here, while the rest of the suite runs
/// alongside.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AIGlobalUnloadCollection
{
    public const string Name = "AI global unload";
}
