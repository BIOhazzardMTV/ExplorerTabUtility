using System;
using System.Collections.Generic;
using H.Hooks;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Hooks;

public sealed class Keyboard : IHook
{
    private readonly LowLevelKeyboardHook _lowLevelKeyboardHook;
    private readonly Func<IReadOnlyList<HotKeyProfile>> _getProfiles;
    public bool IsHookActive => _lowLevelKeyboardHook.IsStarted;
    public event Action<IReadOnlyList<HotKeyEventArgs>>? OnHotKeyProfilesTriggered;

    public Keyboard(Func<IReadOnlyList<HotKeyProfile>> getProfiles)
    {
        _getProfiles = getProfiles;
        _lowLevelKeyboardHook = new LowLevelKeyboardHook { Handling = true };
        _lowLevelKeyboardHook.Down += LowLevelKeyboardHook_Down;
    }

    public void StartHook() => _lowLevelKeyboardHook.Start();
    public void StopHook() => _lowLevelKeyboardHook.Stop();

    private void LowLevelKeyboardHook_Down(object? sender, KeyboardEventArgs e)
    {
        var handler = OnHotKeyProfilesTriggered;
        if (handler == null) return;

        bool? isFileExplorerForeground = null;
        nint handle = 0;
        List<HotKeyEventArgs>? matches = null;
        foreach (var profile in _getProfiles())
        {
            // Skip disabled, empty or mouse
            if (!profile.IsEnabled || profile.IsMouse || profile.HotKeys is null || profile.HotKeys.Length == 0)
                continue;

            // Skip if keys do not match
            if (!e.Keys.Are(profile.HotKeys)) continue;

            // Let's see if we need to check File Explorer
            if (profile.Scope == HotkeyScope.FileExplorer)
            {
                // Check if File Explorer is foreground (only once)
                isFileExplorerForeground ??= Helper.IsFileExplorerForeground(out handle);

                if (isFileExplorerForeground == false)
                {
                    handle = 0; // Reset handle if not File Explorer
                    continue;
                }
            }

            // Set handled value.
            if (profile.IsHandled) e.IsHandled = true;
            
            (matches ??= []).Add(new HotKeyEventArgs(profile, handle));
        }
        if (matches != null) handler(matches);
    }

    public void Dispose()
    {
        StopHook();
        _lowLevelKeyboardHook.Dispose();
    }
}
