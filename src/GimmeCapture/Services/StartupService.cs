using Microsoft.Win32;
using System;
using System.IO;

namespace GimmeCapture.Services;

public class StartupService
{
    private const string AppName = "GimmeCapture";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetStartup(bool runOnStartup)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            var existingValue = key.GetValue(AppName) as string;
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            
            var expectedValue = $"\"{exePath}\"";

            if (runOnStartup)
            {
                // Only write if not exists or different
                if (existingValue != expectedValue)
                {
                    key.SetValue(AppName, expectedValue);
                }
            }
            else
            {
                // Only delete if exists
                if (existingValue != null)
                {
                    key.DeleteValue(AppName);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error setting startup: {ex.Message}");
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return false;

            return key.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }
}
