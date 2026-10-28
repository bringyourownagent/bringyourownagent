namespace BringYourOwnAgent;

/// <summary>
/// BYOA (Bring Your Own Agent): an early draft reference implementation.
/// <see cref="Manifest.Validate(System.Text.Json.JsonElement)"/> checks a requester's manifest;
/// <see cref="Gate.CheckCall(System.Text.Json.JsonElement, System.Text.Json.JsonElement, System.Text.Json.JsonElement, System.Text.Json.JsonElement?, PurposeJudge?)"/>
/// is the gate for one call. Problems are data, never exceptions. https://bringyourownagent.org
/// </summary>
public static class LibraryInfo
{
    /// <summary>Version of this library (not of the protocol). The Python and Node.js libraries of the same version implement the same rules.</summary>
    public static string Version => "0.0.3";
}
