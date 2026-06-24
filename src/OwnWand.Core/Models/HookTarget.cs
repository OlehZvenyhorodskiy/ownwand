namespace OwnWand.Core.Models;

/// <summary>
/// Describes where to hook into a Unity game's code.
/// </summary>
public class HookTarget
{
    public string Namespace { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string PatchType { get; set; } = "Prefix"; // Prefix, Postfix, Transpiler
    public string Action { get; set; } = "skip"; // skip, set_field, multiply_field, nop, override_return
    public string? Field { get; set; }
    public string? Value { get; set; }

    // Native pattern scanning parameters for Unreal Engine / C++ games
    public string? Pattern { get; set; }
    public int? Offset { get; set; }
    public string? PatchBytes { get; set; } // Hex string representation e.g. "90 90 90"
}
