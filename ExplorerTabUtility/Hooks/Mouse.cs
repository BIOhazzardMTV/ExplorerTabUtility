using System;
using System.Collections.Generic;
using H.Hooks;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Hooks;

public sealed class Mouse : IHook
{
    private int _lastClickTime;
    private Key _lastClickKey;
    private readonly LowLevelMouseHook _lowLevelMouseHook;
    private readonly Func<IReadOnlyList<HotKeyProfile>> _getProfiles;
    public bool IsHookActive => _lowLevelMouseHook.IsStarted;
    public event Action<IReadOnlyList<HotKeyEventArgs>>? OnHotKeyProfilesTriggered;

    public Mouse(Func<IReadOnlyList<HotKeyProfile>> getProfiles)
    {
        _getProfiles = getProfiles;
        _lowLevelMouseHook = new LowLevelMouseHook { AddKeyboardKeys = true };
        _lowLevelMouseHook.Down += LowLevelMouseHook_Down;
    }

    public void StartHook() => _lowLevelMouseHook.Start();
    public void StopHook() => _lowLevelMouseHook.Stop();

    private void LowLevelMouseHook_Down(object? sender, MouseEventArgs e)
    {
        var handler = OnHotKeyProfilesTriggered;
        if (handler == null) return;

        var isDoubleClick = IsDoubleClick(e.CurrentKey);

        bool? isFileExplorerForeground = null;
        nint handle = 0;
        List<HotKeyEventArgs>? matches = null;
        foreach (var profile in _getProfiles())
        {
            // Skip disabled, empty or non mouse
            if (!profile.IsMouse || !profile.IsEnabled || profile.HotKeys is null || profile.HotKeys.Length == 0)
                continue;
            
            // Skip if it requires double-click and it is not
            if (profile.IsDoubleClick && !isDoubleClick) continue;
            
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
            
            (matches ??= []).Add(new HotKeyEventArgs(profile, handle, e.Position));
        }
        if (matches != null) handler(matches);
    }

    private bool IsDoubleClick(Key currentKey)
    {
        var isDoubleClick = false;
        
        var now = Environment.TickCount;
        if (now - _lastClickTime < 500 && _lastClickKey == currentKey)
            isDoubleClick = true;
        
        _lastClickTime = now;
        _lastClickKey = currentKey;
        return isDoubleClick;
    }

    public void Dispose()
    {
        StopHook();
        _lowLevelMouseHook.Dispose();
    }
}
