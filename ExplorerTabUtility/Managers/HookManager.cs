using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using System.ComponentModel;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Hooks;
using ExplorerTabUtility.WinAPI;
using ExplorerTabUtility.UI.Views;

namespace ExplorerTabUtility.Managers;

[Fody.ConfigureAwait(true)]
public sealed class HookManager
{
    private readonly Mouse _mouseHook;
    private readonly Keyboard _keyboardHook;
    private readonly ExplorerWatcher _windowHook;
    private readonly SynchronizationContext _syncContext;
    private readonly SemaphoreSlim _shortcutQueue = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;
    public event Action? OnVisibilityToggled;
    public event Action? OnWindowHookToggled;
    public event Action? OnReuseTabsToggled;
    public event Action? OnShellInitialized;

    public HookManager(ProfileManager profileManager)
    {
        _syncContext = SynchronizationContext.Current!;

        _windowHook = new ExplorerWatcher();
        _mouseHook = new Mouse(profileManager.GetProfiles);
        _keyboardHook = new Keyboard(profileManager.GetProfiles);

        profileManager.KeybindingsHookStarted += KeybindingStarted;
        profileManager.KeybindingsHookStopped += KeybindingStopped;
        _keyboardHook.OnHotKeyProfilesTriggered += OnHotKeyProfilesTriggered;
        _mouseHook.OnHotKeyProfilesTriggered += OnHotKeyProfilesTriggered;
        _windowHook.OnShellInitialized += () => OnShellInitialized?.Invoke();
    }

    public void StartMouseHook() => ChangeHookStatus(_mouseHook, true);
    public void StopMouseHook() => ChangeHookStatus(_mouseHook, false);
    public void StartKeyboardHook() => ChangeHookStatus(_keyboardHook, true);
    public void StopKeyboardHook() => ChangeHookStatus(_keyboardHook, false);
    public void StartWindowHook() => ChangeHookStatus(_windowHook, true);
    public void StopWindowHook() => ChangeHookStatus(_windowHook, false);
    public void SetReuseTabs(bool value) => _windowHook.SetReuseTabs(value);
    public void SetRestorePreviousWindows(bool value) => _windowHook.SetRestorePreviousWindows(value);

    private void OnHotKeyProfilesTriggered(IReadOnlyList<HotKeyEventArgs> profiles) =>
        _syncContext.Post(state => { _ = RunHotKeyChain(profiles); }, null);

    private async Task RunHotKeyChain(IReadOnlyList<HotKeyEventArgs> profiles)
    {
        if (_disposed) return;
        try
        {
            await _shortcutQueue.WaitAsync(_shutdown.Token);
            try
            {
                foreach (var profile in profiles)
                {
                    if (profile.Profile.Delay > 0) await Task.Delay(profile.Profile.Delay, _shutdown.Token);
                    if (_disposed) return;
                    try { await RunHotKeyAction(profile); }
                    catch (Exception ex) when (ex is COMException or InvalidComObjectException or IOException or Win32Exception or UnauthorizedAccessException)
                    {
                        System.Diagnostics.Trace.TraceWarning($"Explorer shortcut interrupted: {ex.Message}");
                    }
                }
            }
            finally { _shortcutQueue.Release(); }
        }
        catch (OperationCanceledException) when (_disposed) { }
    }

    private async Task RunHotKeyAction(HotKeyEventArgs e)
    {
        switch (e.Profile.Action)
        {
            case HotKeyAction.Open:
                await _windowHook.Open(e.Profile.Path, e.Profile.IsAsTab, e.ForegroundWindow);
                break;

            case HotKeyAction.Duplicate:
                await _windowHook.DuplicateActiveTab(e.ForegroundWindow, e.Profile.IsAsTab);
                break;

            case HotKeyAction.ReopenClosed:
                await _windowHook.ReopenClosedTab(e.Profile.IsAsTab, e.ForegroundWindow);
                break;

            case HotKeyAction.DetachTab:
                await _windowHook.DetachCurrentTab(e.ForegroundWindow);
                break;

            case HotKeyAction.SetTargetWindow:
                _windowHook.SetTargetWindow(e.ForegroundWindow);
                break;

            case HotKeyAction.NavigateBack:
                NavigateBackForward(e.ForegroundWindow, e.MousePosition, isForward: false);
                break;

            case HotKeyAction.NavigateUp:
                NavigateUp(e.ForegroundWindow, e.MousePosition);
                break;
            
            case HotKeyAction.NavigateForward:
                NavigateBackForward(e.ForegroundWindow, e.MousePosition, isForward: true);
                break;

            case HotKeyAction.ToggleReuseTabs:
                OnReuseTabsToggled?.Invoke();
                break;

            case HotKeyAction.ToggleWinHook:
                OnWindowHookToggled?.Invoke();
                break;

            case HotKeyAction.ToggleVisibility:
                OnVisibilityToggled?.Invoke();
                break;

            case HotKeyAction.TabSearch:
                new TabSearchPopup(_windowHook).Show();
                break;

            case HotKeyAction.SnapRight:
            case HotKeyAction.SnapLeft:
            case HotKeyAction.SnapUp:
            case HotKeyAction.SnapDown:
                SnapWindow(e.Profile.Action, e.Profile.Scope == HotkeyScope.FileExplorer ? e.ForegroundWindow : 0);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(e.Profile.Action),
                    e.Profile.Action,
                    @"Invalid profile action");
        }
    }
    private void KeybindingStarted()
    {
        StopMouseHook();
        StopKeyboardHook();
    }
    private void KeybindingStopped()
    {
        if (SettingsManager.IsMouseHookActive) StartMouseHook();
        if (SettingsManager.IsKeyboardHookActive) StartKeyboardHook();
    }
    private void NavigateBackForward(nint foregroundWindow, Point? mousePosition, bool isForward)
    {
        if (foregroundWindow == 0) return;

        if (mousePosition is not { } position)
            _windowHook.NavigateBackForward(foregroundWindow, isForward);
        else if (Helper.IsExplorerEmptySpace(position))
            KeyboardSimulator.ModifiedKeyStroke(VirtualKey.Alt, isForward ? VirtualKey.Right : VirtualKey.Left);
    }
    private void NavigateUp(nint foregroundWindow, Point? mousePosition)
    {
        if (foregroundWindow == 0) return;

        if (mousePosition is not { } position)
            KeyboardSimulator.ModifiedKeyStroke(VirtualKey.Alt, VirtualKey.Up);
        else if (Helper.IsExplorerEmptySpace(position))
            KeyboardSimulator.ModifiedKeyStroke(VirtualKey.Alt, VirtualKey.Up);
    }

    private static void SnapWindow(HotKeyAction direction, nint window)
    {
        var snapKey = GetSnapKey(direction);
        if (snapKey == VirtualKey.None) return;
        if (window != 0 && WinApi.GetForegroundWindow() != window)
        {
            if (!Helper.IsFileExplorerWindow(window)) return;
            WinApi.RestoreWindowToForeground(window);
            if (WinApi.GetForegroundWindow() != window) return;
        }

        var inputs = new List<INPUT>();
        // Remove any currently pressed modifiers (Ctrl, Shift, Alt, Win)
        inputs.AddUpEventsForCurrentlyPressedModifiers();

        // Press Windows key
        inputs.AddKeyDown(VirtualKey.LWin);

        // For up and down press Alt key
        if (snapKey is VirtualKey.Up or VirtualKey.Down)
            inputs.AddKeyDown(VirtualKey.Alt);

        // Press the snap key
        inputs.AddKeyPress(snapKey);

        // For up and down release Alt key
        if (snapKey is VirtualKey.Up or VirtualKey.Down)
            inputs.AddKeyUp(VirtualKey.Alt);

        // Release Windows key
        inputs.AddKeyUp(VirtualKey.LWin);

        // Re-add any currently pressed modifiers
        inputs.AddDownEventsForCurrentlyPressedModifiers();

        KeyboardSimulator.SendInputs(inputs.ToArray());
    }
    private static VirtualKey GetSnapKey(HotKeyAction direction)
    {
        return direction switch
        {
            HotKeyAction.SnapRight => VirtualKey.Right,
            HotKeyAction.SnapLeft => VirtualKey.Left,
            HotKeyAction.SnapUp => VirtualKey.Up,
            HotKeyAction.SnapDown => VirtualKey.Down,
            _ => VirtualKey.None
        };
    }

    private static void ChangeHookStatus(IHook hook, bool isActive)
    {
        if (hook.IsHookActive == isActive) return;

        if (isActive)
            hook.StartHook();
        else
            hook.StopHook();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _keyboardHook.Dispose();
        _mouseHook.Dispose();
        _windowHook.Dispose();
    }
}
