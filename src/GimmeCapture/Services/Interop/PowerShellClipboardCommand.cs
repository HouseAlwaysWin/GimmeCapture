using System;
using System.IO;

namespace GimmeCapture.Services.Interop;

/// <summary>
/// Builds the <c>powershell.exe</c> command that copies a file to the clipboard as a file drop. Kept
/// separate and pure so the path escaping — which prevents a filename containing a quote from breaking the
/// command or injecting further PowerShell — is unit-testable.
/// </summary>
public static class PowerShellClipboardCommand
{
    /// <summary>
    /// Windows PowerShell by its full System32 path. Starting it as a bare <c>"powershell"</c> made Windows search
    /// for it — and PowerShell does not live in the root of System32, so the search reached PATH, whose FIRST entry
    /// is the user-writable AI runtime folder (<c>NativeResolverService</c> prepends it). Any
    /// <c>powershell.exe</c> dropped there would have run in its place on every "copy recording".
    /// </summary>
    public static string ExecutablePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    /// <summary>
    /// Arguments for <c>powershell.exe</c> that copy <paramref name="filePath"/> to the clipboard. The path
    /// is escaped for PowerShell's single-quoted string literal (<c>'</c> → <c>''</c>), so a quote in the
    /// filename can neither terminate the string early nor inject commands — single-quoted strings perform no
    /// expansion. (Windows filenames cannot contain <c>"</c>, so the outer double quotes are safe.)
    /// <c>-LiteralPath</c>, not <c>-Path</c>: <c>-Path</c> treats <c>[ ]</c> as wildcard characters, so a file
    /// named like <c>clip[1].mp4</c> matched nothing (or the wrong file) and silently copied nothing.
    /// </summary>
    public static string BuildSetClipboardArguments(string filePath)
    {
        var escaped = (filePath ?? string.Empty).Replace("'", "''");
        return $"-noprofile -command \"Set-Clipboard -LiteralPath '{escaped}'\"";
    }
}
