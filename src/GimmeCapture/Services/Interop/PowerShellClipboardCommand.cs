namespace GimmeCapture.Services.Interop;

/// <summary>
/// Builds the <c>powershell.exe</c> command that copies a file to the clipboard as a file drop. Kept
/// separate and pure so the path escaping — which prevents a filename containing a quote from breaking the
/// command or injecting further PowerShell — is unit-testable.
/// </summary>
public static class PowerShellClipboardCommand
{
    /// <summary>
    /// Arguments for <c>powershell.exe</c> that copy <paramref name="filePath"/> to the clipboard. The path
    /// is escaped for PowerShell's single-quoted string literal (<c>'</c> → <c>''</c>), so a quote in the
    /// filename can neither terminate the string early nor inject commands — single-quoted strings perform no
    /// expansion. (Windows filenames cannot contain <c>"</c>, so the outer double quotes are safe.)
    /// </summary>
    public static string BuildSetClipboardArguments(string filePath)
    {
        var escaped = (filePath ?? string.Empty).Replace("'", "''");
        return $"-noprofile -command \"Set-Clipboard -Path '{escaped}'\"";
    }
}
