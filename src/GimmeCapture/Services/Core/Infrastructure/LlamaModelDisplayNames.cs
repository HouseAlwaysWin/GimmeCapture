namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Pure mapping from a Llama translation model id to its user-facing display name.
/// Extracted from <c>MainWindowViewModel.Settings</c> to remove the duplicated
/// id→name pairs and allow unit testing without a ViewModel. Behavior-preserving.
/// </summary>
public static class LlamaModelDisplayNames
{
    /// <summary>
    /// Returns the display name for the given model id. Unknown ids return the id itself.
    /// </summary>
    public static string Get(string modelId) => modelId switch
    {
        "translategemma-4b-it" => "TranslateGemma 4B",
        "gemma-3-4b-it-q4" => "Gemma 3 4B",
        "translategemma-12b-it" => "TranslateGemma 12B (Experimental)",
        _ => modelId
    };
}
