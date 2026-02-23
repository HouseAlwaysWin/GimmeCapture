using System;
using System.Collections.Generic;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Services.Core.Infrastructure;

public class HotkeyMappingService
{
    public void UpdateViewModelHotkey(MainWindowViewModel vm, string tag, string hotkeyStr)
    {
        if (vm == null || string.IsNullOrEmpty(tag)) return;

        // Use reflection to find the property on MainWindowViewModel
        var prop = typeof(MainWindowViewModel).GetProperty(tag);
        if (prop != null && prop.PropertyType == typeof(string) && prop.CanWrite)
        {
            prop.SetValue(vm, hotkeyStr);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[HotkeyMappingService] Unknown hotkey tag or property not found: {tag}");
        }
    }
}
