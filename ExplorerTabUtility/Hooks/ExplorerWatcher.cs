using Shell32;
using SHDocVw;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;
using ExplorerTabUtility.UI.Views;

namespace ExplorerTabUtility.Hooks;

using WindowEntry = DualKeyEntry<InternetExplorer, nint?, WindowInfo>;

[Fody.ConfigureAwait(true)]
public class ExplorerWatcher : IHook
{
    private const int HistoryLimit = 100;
    private static bool _instanceRunning;
    private static Guid _shellBrowserGuid = typeof(IShellBrowser).GUID;

    private ShellWindows _shellWindows = null!;
    private ShellPathComparer _shellPathComparer = null!;
    private readonly StaTaskScheduler _staTaskScheduler;
    private nint _mainWindowHandle;
    private readonly ConcurrentDictionary<nint, byte> _processedHWnds = new();
    private readonly Dictionary<nint, bool> _hiddenWindows = new();
    private readonly DualKeyDictionary<InternetExplorer, nint?, WindowInfo> _windowEntryDict = [];
    private readonly List<WindowRecord> _closedWindows = new();
    private readonly HashSet<WindowRecord> _reopening = [];
    private WindowRecord[] _pendingRestore = [];
    private WindowRecord[] _lastSession = [];
    private WindowRecord[] _lastHistory = [];
    private readonly Dictionary<nint, Guid> _sessionGroups = new();
    private bool _historyLoaded;
    private bool _restoreOffered;
    private HashSet<nint>? _windowsBeforeOpen;
    private WindowRecord? _requestedWindow;
    private readonly DispatcherTimer _sessionTimer;
    private bool _restoring;
    private readonly object _windowEntryDictLock = new(), _closedWindowsLock = new();
    private readonly SemaphoreSlim _toOpenWindowsLock = new(1);
    private readonly ProcessWatcher _processWatcher;
    private int _mainExplorerProcessId;
    private readonly DispatcherTimer _explorerCheckTimer;
    private bool _disposed;

    private nint _eventObjectShowHookId;
    private WinEventDelegate? _eventObjectShowHookCallback;
    private DShellWindowsEvents_WindowRegisteredEventHandler? _windowRegisteredHandler;

    private string _defaultLocation = null!;
    private bool _reuseTabs = true;
    private bool _isForcingTabs;
    public bool IsHookActive => _isForcingTabs;
    public event Action? OnShellInitialized;

    public ExplorerWatcher()
    {
        if (_instanceRunning)
            throw new InvalidOperationException("Only one instance of ExplorerWatcher is allowed at a time.");
        _instanceRunning = true;

        _staTaskScheduler = new StaTaskScheduler();
        _processWatcher = new ProcessWatcher("explorer");
        _processWatcher.ProcessTerminated += OnExplorerProcessTerminated;
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _sessionTimer.Tick += (_, _) =>
        {
            if (_shellWindows == null || _restoring) return;
            if (!_restoreOffered)
            {
                _restoreOffered = true;
                _ = RestorePreviousWindows(_shellWindows);
            }
            PersistWindows();
        };
        _explorerCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _explorerCheckTimer.Tick += CheckForMainExplorer;
        _explorerCheckTimer.Start();
        _explorerCheckTimer.Dispatcher.BeginInvoke(() => CheckForMainExplorer(null, EventArgs.Empty));
    }

    public void StartHook()
    {
        if (_isForcingTabs) return;
        _isForcingTabs = true;
    }

    public void StopHook()
    {
        if (!_isForcingTabs) return;
        _isForcingTabs = false;
    }
    public void SetReuseTabs(bool reuseTabs) => _reuseTabs = reuseTabs;

    public void SetRestorePreviousWindows(bool enabled)
    {
        foreach (var (window, info) in _windowEntryDict.ToArray<WindowEntry>())
        {
            try
            {
                SetNavigationTracking(window, info, enabled);
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                Trace.TraceWarning($"Could not update Explorer navigation tracking: {ex.Message}");
            }
        }
        if (!enabled) _pendingRestore = [];
        PersistWindows();
    }

    public void ClearClosedWindows()
    {
        lock (_closedWindowsLock)
        {
            SettingsManager.ClearClosedWindows();
            _closedWindows.Clear();
            _lastHistory = [];
        }
    }

    public IReadOnlyCollection<WindowRecord> GetWindows()
    {
        var result = new List<WindowRecord>();
        
        // Add open windows
        lock (_windowEntryDictLock)
            result.AddRange(
                _windowEntryDict.Keys.Select(ie => new WindowRecord(GetLocation(ie), new IntPtr(ie.HWND), GetSelectedItems(ie), ie.LocationName)));
        
        // Add closed windows in reverse order (last closed on top)
        lock (_closedWindowsLock)
            result.AddRange(_closedWindows.AsEnumerable().Reverse());
        
        return result.GroupBy(w => w.Location).Select(g => g.First()).ToList();
    }

    public async Task SwitchTo(string location, nint windowHandle = 0, string[]? selectedItems = null, bool asTab = true, bool duplicate = false)
    {
        var windowToOpen = new WindowRecord(location, windowHandle, selectedItems);
        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowToOpen);
            return;
        }

        await OpenTabNavigateWithSelection(windowToOpen, windowHandle, duplicate, true);
    }
    
    public nint SearchForTab(string targetPath, nint excludedWindow = 0)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(targetPath);
            if (targetPidl == 0) return 0;

            foreach (var (window, windowInfo, tabHandle) in _windowEntryDict)
            {
                // Make sure it is not the newly created window
                if (!Helper.IsTimeUp(windowInfo.CreatedAt, 2_000) || !tabHandle.HasValue || tabHandle.Value == 0)
                    continue;

                if (excludedWindow != 0 && new IntPtr(window.HWND) == excludedWindow) continue;
                var comparePath = windowInfo.Location ?? GetLocation(window);

                if (_shellPathComparer.IsEquivalent(targetPath, comparePath, targetPidl))
                    return tabHandle.Value;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }
    public async Task<bool> SelectTabByHandle(nint windowHandle, nint tabHandle)
    {
        var tabs = Helper.GetAllExplorerTabs(windowHandle).ToArray();
        if (tabs.Length == 0) return false;

        var activeTab = tabs[0];
        for (var i = 0; i < tabs.Length; i++)
        {
            if (activeTab == tabHandle) break;

            if (!WinApi.SendExplorerCommand(windowHandle, 0xA221, i + 1)) return false;

            // ReSharper disable once AccessToModifiedClosure
            activeTab = await Helper.DoUntilConditionAsync(
                () => WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null),
                h => h != activeTab);
        }
        return activeTab == tabHandle;
    }
    public void SelectLastTab(nint windowHandle)
    {
        var count = Helper.GetAllExplorerTabs(windowHandle).Count();
        SelectTabByIndex(windowHandle, count - 1);
    }
    public void SelectTabByIndex(nint windowHandle, int index)
    {
        // Send 0xA221 magic command (CTRL + 1...n)
        if (index >= 0) WinApi.SendExplorerCommand(windowHandle, 0xA221, index + 1);
    }
    public async Task RequestToOpenNewTab(nint windowHandle, bool bringToFront = false, bool lockToOpenWindows = true)
    {
        if (bringToFront && windowHandle == 0)
            windowHandle = GetMainWindowHWnd(0);

        if (windowHandle == 0)
        {
            await OpenNewWindowWithSelection(new WindowRecord(string.Empty), lockToOpenWindows);
            return;
        }

        var tabHandle = WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
        if (tabHandle == 0) return;

        // Send 0xA21B magic command (CTRL + T)
        if (!WinApi.SendExplorerCommand(tabHandle, 0xA21B, 0)) return;

        if (bringToFront)
            WinApi.RestoreWindowToForeground(windowHandle);
    }
    public async Task Open(string? location, bool asTab, nint windowHandle)
    {
        var normalizedPath = Helper.NormalizeLocation(location ?? string.Empty);
        
        if (normalizedPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            System.IO.File.Exists(normalizedPath))
        {
            try
            {
                Helper.BypassWinForegroundRestrictions();
                Process.Start(new ProcessStartInfo(normalizedPath) { UseShellExecute = true });
                return;
            }
            catch
            {
                //
            }
        }
        
        if (!asTab)
        {
            await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
            return;
        }

        if (string.IsNullOrWhiteSpace(normalizedPath) && !_reuseTabs)
        {
            await RequestToOpenNewTab(windowHandle, bringToFront: true);
            return;
        }

        if (_windowEntryDict.Count > 0)
        {
            await OpenTabNavigateWithSelection(new WindowRecord(normalizedPath, windowHandle), windowHandle);
            return;
        }

        await OpenNewWindowWithSelection(new WindowRecord(normalizedPath));
    }
    public async Task DuplicateActiveTab(nint windowHandle, bool asTab)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        if (!_windowEntryDict.TryGetValue(activeTabHandle, out WindowEntry entry)) return;

        var location = entry.Value.Location ?? GetLocation(entry.PrimaryKey);
        var selectedItems = GetSelectedItems(entry.PrimaryKey);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        if (!asTab)
        {
            await OpenNewWindowWithSelection(windowRecord);
            return;
        }

        await OpenTabNavigateWithSelection(windowRecord, windowHandle, isDuplicate: true);
    }
    public async Task ReopenClosedTab(bool asTab, nint windowHandle = 0)
    {
        WindowRecord? closedWindow;
        lock (_closedWindowsLock)
        {
            closedWindow = _closedWindows.LastOrDefault(w => w.Location != _defaultLocation && !_reopening.Contains(w));
            if (closedWindow == null) return;
            _reopening.Add(closedWindow);
        }

        try
        {
            var opened = asTab
                ? await OpenTabNavigateWithSelection(closedWindow, windowHandle)
                : await OpenNewWindowWithSelection(closedWindow) != 0;
            if (opened)
            {
                lock (_closedWindowsLock) _closedWindows.Remove(closedWindow);
                PersistWindows();
            }
        }
        finally
        {
            lock (_closedWindowsLock) _reopening.Remove(closedWindow);
        }
    }
    public async Task DetachCurrentTab(nint windowHandle)
    {
        if (Helper.GetAllExplorerTabs(windowHandle).Take(2).Count() < 2)
            return;

        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        if (!_windowEntryDict.TryGetValue(activeTabHandle, out WindowEntry entry)) return;

        var location = entry.Value.Location ?? GetLocation(entry.PrimaryKey);
        var selectedItems = GetSelectedItems(entry.PrimaryKey);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);

        var shell = _shellWindows;
        if (await OpenNewWindowWithSelection(windowRecord) == 0 || !ReferenceEquals(_shellWindows, shell) ||
            !_windowEntryDict.TryGetValue(activeTabHandle, out WindowEntry current) ||
            !ReferenceEquals(entry.PrimaryKey, current.PrimaryKey) || GetActiveTabHandle(windowHandle) != activeTabHandle ||
            !_shellPathComparer.IsEquivalent(location, GetLocation(current.PrimaryKey))) return;

        // Close the same tab after Explorer confirms the destination window.
        WinApi.SendExplorerCommand(activeTabHandle, 0xA021, 1);
    }
    public void SetTargetWindow(nint windowHandle)
    {
        if (Helper.IsFileExplorerWindow(windowHandle))
            _mainWindowHandle = windowHandle;
    }
    public void NavigateBackForward(nint windowHandle, bool isForward)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        try
        {
            if (isForward) window?.GoForward();
            else window?.GoBack();
        }
        catch
        {
            // Will throw if there is no further history
        }
    }

    private void PreventWindowHiding(nint hWnd)
    {
        if (_processedHWnds.TryAdd(hWnd, 0))
        {
            // Schedule removal after a short delay
            _ = Task.Delay(7_000).ContinueWith(t => _processedHWnds.TryRemove(hWnd, out _), TaskScheduler.Default);
        }
    }
    private void OnWindowShown(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dWmsEventTime)
    {
        if (_disposed || !_isForcingTabs || idObject != 0 || idChild != 0) return;
        
        // Check if the hWnd was processed by OnShellWindowRegistered
        if (_windowsBeforeOpen != null || _processedHWnds.ContainsKey(hWnd) || _hiddenWindows.ContainsKey(hWnd)) return;
        
        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return;

        if (_windowEntryDict.Count == 0 || Helper.IsCtrlShiftDown() ||
            _windowEntryDict.OptionalKeys.Any(tab => tab.HasValue && WinApi.GetParent(tab.Value) == hWnd))
            return;

        _hiddenWindows.Add(hWnd, false);
        Helper.HideWindow(hWnd, SettingsManager.HaveThemeIssue);
        _ = ShowIfUnregistered(hWnd);
    }

    private async Task ShowIfUnregistered(nint window)
    {
        await Task.Delay(3000);
        if (_hiddenWindows.TryGetValue(window, out var registered) && !registered)
            RestoreHiddenWindow(window);
    }

    private void RestoreHiddenWindow(nint window)
    {
        if (!_hiddenWindows.Remove(window)) return;
        PreventWindowHiding(window);
        if (!Helper.HiddenWindows.TryGetValue(window, out var position)) return;
        Helper.ShowWindow(window, removeCache: true);
        if (position == null)
            Helper.UpdateWindowLayered(window, remove: true);
    }
    private InternetExplorer? GetRecentlyCreatedWindow(out WindowInfo? windowInfo)
    {
        windowInfo = null;
        if (_shellWindows == null) return null;
        // When a new window is registered, it's typically the last in the collection
        var count = _shellWindows.Count;
        for (var i = count - 1; i >= 0; i--)
        {
            if (_shellWindows.Item(i) is not InternetExplorer window) continue;

            lock (_windowEntryDictLock)
            {
                if (_windowEntryDict.Keys.Contains(window)) continue;
                if (window.GetProperty("seenBefore") is not null) continue;
                window.PutProperty("seenBefore", true);

                windowInfo = new WindowInfo();
                _windowEntryDict.Add(window, windowInfo);

                if (_windowEntryDict.Count == 1)
                    _mainWindowHandle = new IntPtr(window.HWND);
                
                return window;
            }
        }

        return null;
    }
    private void OnShellWindowRegistered(int _)
    {
        var shell = _shellWindows;
        if (shell == null) return;
        // Explorer can reject nested COM calls until WindowRegistered returns.
        _explorerCheckTimer.Dispatcher.BeginInvoke(() => HandleShellWindowRegistered(shell));
    }

    private async void HandleShellWindowRegistered(ShellWindows shell)
    {
        var showAgain = true;
        nint hWnd = 0;
        try
        {
            if (!ReferenceEquals(_shellWindows, shell)) return;
            var shouldOpenAsWindow = Helper.IsCtrlShiftDown();

            WindowInfo windowInfo = null!;
            var window = await Helper.DoUntilNotDefaultAsync(
                () => ReferenceEquals(_shellWindows, shell) ? GetRecentlyCreatedWindow(out windowInfo!) : null,
                2_500, 70, continueOnCapturedContext: true);
            if (window == null) return;

            hWnd = new IntPtr(window.HWND);
            if (_hiddenWindows.ContainsKey(hWnd))
                _hiddenWindows[hWnd] = true;
            GetTabHandle(window);
            
            if (shouldOpenAsWindow)
            {
                PreventWindowHiding(hWnd);
                HookWindowEvents(window, windowInfo);
                return;
            }
            
            var location = GetLocation(window);

            var request = _requestedWindow;
            if (request != null && _windowsBeforeOpen?.Contains(hWnd) == false)
            {
                // WindowRegistered can arrive before the new window reaches its requested location.
                await Helper.DoUntilNotDefaultAsync(() =>
                    !ReferenceEquals(_shellWindows, shell) || !ReferenceEquals(_requestedWindow, request) ||
                    _shellPathComparer.IsEquivalent(request.Location, location = GetLocation(window)),
                    2500, 50, continueOnCapturedContext: true);
                if (!ReferenceEquals(_shellWindows, shell)) return;
                if (_shellPathComparer.IsEquivalent(request.Location, location))
                {
                    PreventWindowHiding(hWnd);
                    HookWindowEvents(window, windowInfo);
                    return;
                }
            }

            //Control Panel
            if (location.StartsWith("shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}"))
            {
                PreventWindowHiding(hWnd);
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            // Check if this is a single tab window and there are other windows
            var shouldReopenAsTab = (_isForcingTabs || _reuseTabs) &&
                                    _windowEntryDict.Count > 1 &&
                                    hWnd != _mainWindowHandle &&
                                    Helper.GetAllExplorerTabs(hWnd).Take(2).Count() == 1;

            if (shouldReopenAsTab)
            {
                _hiddenWindows[hWnd] = true;
                Helper.HideWindow(hWnd, SettingsManager.HaveThemeIssue);
            }
            else
                PreventWindowHiding(hWnd);

            // Check if it is a detached tab
            var isRecentlyClosed = TryGetRecentlyClosedWindow(location, out var closedWindow);
            if (isRecentlyClosed)
                SelectItems(window, closedWindow!.SelectedItems);

            shouldReopenAsTab = shouldReopenAsTab && !isRecentlyClosed;

            if (shouldReopenAsTab)
            {
                var record = new WindowRecord(location, hWnd, GetSelectedItems(window));
                if (await OpenTabNavigateWithSelection(record, _mainWindowHandle, excludedWindow: hWnd) &&
                    ReferenceEquals(_shellWindows, shell) && _shellPathComparer.IsEquivalent(record.Location, GetLocation(window)))
                {
                    window.Quit();
                    showAgain = false;
                    RemoveWindowAndUnhookEvents(window, windowInfo);
                }
                else if (ReferenceEquals(_shellWindows, shell))
                {
                    HookWindowEvents(window, windowInfo);
                }
                return;
            }

            // Track the new tab before waiting for a possible reattach to finish.
            HookWindowEvents(window, windowInfo);

            // OnQuit might fire after ShellWindowRegistered in case of reattached tab (and there were selected files)
            if (!isRecentlyClosed)
            {
                isRecentlyClosed = await Helper.DoUntilNotDefaultAsync(() => TryGetRecentlyClosedWindow(location, out closedWindow),
                    700, 50, continueOnCapturedContext: true);
                if (isRecentlyClosed && ReferenceEquals(_shellWindows, shell))
                    SelectItems(window, closedWindow!.SelectedItems);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Trace.TraceWarning($"Explorer window registration interrupted: {ex.Message}");
        }
        finally
        {
            if (showAgain)
            {
                RestoreHiddenWindow(hWnd);
            }
            else
            {
                _hiddenWindows.Remove(hWnd);
                Helper.HiddenWindows.TryRemove(hWnd, out _);
            }
        }
    }
    private void HookWindowEvents(InternetExplorer window, WindowInfo windowInfo)
    {
        // Create strongly-typed handlers so we can remove them later
        windowInfo.OnQuitHandler = () =>
        {
            try
            {
                var location = windowInfo.Location ?? GetLocation(window);
                var locationName = windowInfo.Name ?? window.LocationName;
                var selectedItems = location == _defaultLocation ? null : GetSelectedItems(window);
                AddHistory(new WindowRecord(location, new IntPtr(window.HWND), selectedItems, locationName));
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                Trace.TraceWarning($"Could not record closed Explorer tab: {ex.Message}");
            }
            finally
            {
                RemoveWindowAndUnhookEvents(window, windowInfo);
            }
        };

        try
        {
            // Subscribe
            window.OnQuit += windowInfo.OnQuitHandler;
            SetNavigationTracking(window, windowInfo, SettingsManager.RestorePreviousWindows);

            // Make sure the window is still alive (User might have closed it immediately after opening it)
            _ = window.HWND;
        }
        catch
        {
            RemoveWindowAndUnhookEvents(window, windowInfo);
        }
    }

    private static void SetNavigationTracking(InternetExplorer window, WindowInfo info, bool enabled)
    {
        if (!enabled)
        {
            if (info.OnNavigateHandler != null)
                window.NavigateComplete2 -= info.OnNavigateHandler;
            info.OnNavigateHandler = null;
            info.Location = null;
            info.Name = null;
            return;
        }

        if (info.OnNavigateHandler != null) return;
        info.Location = GetLocation(window);
        info.Name = window.LocationName;
        info.WindowHandle = new IntPtr(window.HWND);
        DWebBrowserEvents2_NavigateComplete2EventHandler handler = (object _, ref object _) =>
        {
            try
            {
                info.Location = GetLocation(window);
                info.Name = window.LocationName;
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                Trace.TraceWarning($"Could not record Explorer navigation: {ex.Message}");
            }
        };
        window.NavigateComplete2 += handler;
        info.OnNavigateHandler = handler;
    }

    private void RemoveWindowAndUnhookEvents(InternetExplorer window, WindowInfo windowInfo, bool useLock = true)
    {
        bool removed;
        if (useLock)
            lock (_windowEntryDictLock) removed = _windowEntryDict.Remove(window);
        else
            removed = _windowEntryDict.Remove(window);
        if (!removed) return;

        try
        {
                if (windowInfo.OnQuitHandler != null) window.OnQuit -= windowInfo.OnQuitHandler;
            if (windowInfo.OnNavigateHandler != null) window.NavigateComplete2 -= windowInfo.OnNavigateHandler;
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Trace.TraceWarning($"Could not unhook Explorer tab: {ex.Message}");
        }

        Marshal.ReleaseComObject(window);
    }

    private async Task RestorePreviousWindows(ShellWindows shell)
    {
        if (_restoring || _pendingRestore.Length == 0 || !SettingsManager.RestorePreviousWindows) return;
        _restoring = true;
        try
        {
            var existingGroups = CaptureOpenWindows().GroupBy(record => record.Handle)
                .ToDictionary(group => group.Key, group => group.ToList());
            var groups = _pendingRestore.GroupBy(record => (record.SessionGroup,
                LegacyHandle: record.SessionGroup == Guid.Empty ? record.Handle : 0)).Select(group => group.ToArray()).ToArray();
            var targets = new nint[groups.Length];
            for (var i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                var target = existingGroups.OrderByDescending(pair =>
                    pair.Value.Count(current => group.Any(record => SameLocation(current, record))))
                    .FirstOrDefault(pair => pair.Value.Any(current => group.Any(record => SameLocation(current, record))));
                if (target.Value == null) continue;

                targets[i] = target.Key;
                var targetGroup = GetSessionGroup(target.Key);
                existingGroups.Remove(target.Key);
                foreach (var record in group)
                {
                    var match = target.Value.FindIndex(current => SameLocation(current, record));
                    if (match < 0)
                    {
                        record.SessionGroup = targetGroup;
                        continue;
                    }
                    target.Value.RemoveAt(match);
                    FinishRestore(record);
                }
            }

            if (_pendingRestore.Length == 0) return;
            var result = CustomMessageBox.Show("Do you want to restore previously opened windows?",
                Constants.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (_disposed || !ReferenceEquals(_shellWindows, shell) || !SettingsManager.RestorePreviousWindows) return;
            if (result != MessageBoxResult.Yes)
            {
                _pendingRestore = [];
                return;
            }

            for (var i = 0; i < groups.Length; i++)
            {
                var target = targets[i];
                foreach (var record in groups[i])
                {
                    if (_disposed || !ReferenceEquals(_shellWindows, shell) || !SettingsManager.RestorePreviousWindows) return;
                    if (!_pendingRestore.Contains(record)) continue;
                    if (target != 0 && !Helper.IsFileExplorerWindow(target)) target = 0;
                    if (target == 0)
                    {
                        target = await OpenNewWindowWithSelection(record);
                        if (target == 0) continue;
                    }
                    else if (!await OpenTabNavigateWithSelection(record, target, isDuplicate: true, requireTarget: true))
                        continue;

                    var targetGroup = GetSessionGroup(target);
                    foreach (var remaining in groups[i].Where(_pendingRestore.Contains))
                        remaining.SessionGroup = targetGroup;
                    FinishRestore(record);
                    PersistWindows();
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Trace.TraceWarning($"Could not restore the previous Explorer session: {ex.Message}");
        }
        finally
        {
            _restoring = false;
            if (!_disposed && ReferenceEquals(_shellWindows, shell)) PersistWindows();
        }
    }

    private static bool SameLocation(WindowRecord first, WindowRecord second) =>
        StringComparer.OrdinalIgnoreCase.Equals(first.Location, second.Location);

    private void FinishRestore(WindowRecord record) =>
        _pendingRestore = _pendingRestore.Where(remaining => !ReferenceEquals(remaining, record)).ToArray();

    private void AddHistory(WindowRecord record)
    {
        lock (_closedWindowsLock)
        {
            if (_closedWindows.Count >= HistoryLimit)
                _closedWindows.RemoveAt(0);
            _closedWindows.Add(record);
        }
    }

    private async Task<nint> OpenNewWindowWithSelection(WindowRecord windowToOpen, bool lockToOpenWindows = true)
    {
        if (lockToOpenWindows) await _toOpenWindowsLock.WaitAsync();
        try
        {
            var shell = _shellWindows;
            var comparer = _shellPathComparer;
            if (_disposed || shell == null) return 0;

            var location = string.IsNullOrWhiteSpace(windowToOpen.Location) ? _defaultLocation : windowToOpen.Location;
            _requestedWindow = new WindowRecord(location);
            _windowsBeforeOpen = new HashSet<nint>(Helper.GetAllExplorerWindows());
            Helper.BypassWinForegroundRestrictions();
            await RunInStaThread(() =>
            {
                var launcher = new Shell();
                try { launcher.ShellExecute(location, "", "", "opennewwindow"); }
                finally { Marshal.ReleaseComObject(launcher); }
            });

            var opened = await Helper.DoUntilNotDefaultAsync(() =>
            {
                if (!ReferenceEquals(_shellWindows, shell)) return null;
                var candidates = _windowEntryDict.ToArray<WindowEntry>()
                    .Where(entry => entry.Value.OnQuitHandler != null && IsRequestedWindow(entry.PrimaryKey))
                    .Select(entry => entry.PrimaryKey).Take(2).ToArray();
                return candidates.Length == 1 ? candidates[0] : null;
            }, 2500, 50, continueOnCapturedContext: true);

            if (opened == null || !ReferenceEquals(_shellWindows, shell)) return 0;
            SelectItems(opened, windowToOpen.SelectedItems);
            return new IntPtr(opened.HWND);

            bool IsRequestedWindow(InternetExplorer window)
            {
                try
                {
                    var handle = new IntPtr(window.HWND);
                    return handle != IntPtr.Zero && !_windowsBeforeOpen!.Contains(handle) &&
                           comparer.IsEquivalent(location, GetLocation(window));
                }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException) { return false; }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Trace.TraceWarning($"Could not open Explorer window: {ex.Message}");
            return 0;
        }
        finally
        {
            _windowsBeforeOpen = null;
            _requestedWindow = null;
            if (lockToOpenWindows) _toOpenWindowsLock.Release();
        }
    }

    private async Task<bool> OpenTabNavigateWithSelection(WindowRecord windowToOpen, nint windowHandle = 0,
        bool isDuplicate = false, bool forceTabReuse = false, nint excludedWindow = 0, bool requireTarget = false)
    {
        var shell = _shellWindows;
        await _toOpenWindowsLock.WaitAsync();
        try
        {
            if (_disposed || !ReferenceEquals(_shellWindows, shell)) return false;
            if ((_reuseTabs || forceTabReuse) && !isDuplicate && _windowEntryDict.Count > 0)
            {
                var existingTab = SearchForTab(windowToOpen.Location, excludedWindow);
                if (existingTab != 0)
                {
                    var parent = WinApi.GetParent(existingTab);
                    if (await SelectTabByHandle(parent, existingTab))
                    {
                        if (_disposed || !ReferenceEquals(_shellWindows, shell)) return false;
                        if (windowToOpen.SelectedItems is { Length: > 0 })
                        {
                            var existingWindow = GetWindowByTabHandle(existingTab);
                            if (existingWindow == null) return false;
                            SelectItems(existingWindow, windowToOpen.SelectedItems);
                        }
                        WinApi.RestoreWindowToForeground(parent);
                        return true;
                    }
                }
            }

            // Get the main window
            if (_disposed || !ReferenceEquals(_shellWindows, shell)) return false;
            if (requireTarget && !Helper.IsFileExplorerWindow(windowHandle)) return false;
            var mainWindowHWnd = windowHandle != excludedWindow && Helper.IsFileExplorerWindow(windowHandle)
                ? windowHandle
                : GetMainWindowHWnd(excludedWindow != 0 ? excludedWindow : windowToOpen.Handle);

            if (mainWindowHWnd == 0 || mainWindowHWnd == excludedWindow)
            {
                if (excludedWindow != 0) return false;
                return !requireTarget && await OpenNewWindowWithSelection(windowToOpen, lockToOpenWindows: false) != 0;
            }

            // Store the current tabs
            var currentTabs = new HashSet<nint>(Helper.GetAllExplorerTabs(mainWindowHWnd));
            var originTab = GetActiveTabHandle(mainWindowHWnd);
            if (originTab == 0 || !WinApi.SendExplorerCommand(originTab, 0xA21B, 0)) return false;

            var ambiguous = false;
            nint newTabHandle = await Helper.DoUntilNotDefaultAsync(() =>
            {
                if (!ReferenceEquals(_shellWindows, shell)) return -1;
                var candidates = Helper.GetAllExplorerTabs(mainWindowHWnd).Where(tab => !currentTabs.Contains(tab)).Take(2).ToArray();
                ambiguous = candidates.Length > 1;
                return ambiguous ? -1 : candidates.SingleOrDefault();
            }, 2_000, continueOnCapturedContext: true);
            if (newTabHandle == 0 || ambiguous || !ReferenceEquals(_shellWindows, shell)) return false;

            // Get the window object
            var window = await Helper.DoUntilNotDefaultAsync(
                () => ReferenceEquals(_shellWindows, shell) ? GetWindowByTabHandle(newTabHandle) : null,
                2_000, 50, continueOnCapturedContext: true);
            // shortcut: Ctrl+T exposes no request identifier, use a correlated Shell API if Explorer adds one.
            if (window == null || !ReferenceEquals(_shellWindows, shell) ||
                GetActiveTabHandle(mainWindowHWnd) != newTabHandle ||
                Helper.GetAllExplorerTabs(mainWindowHWnd).Any(tab => tab != newTabHandle && !currentTabs.Contains(tab)))
                return false;

            var destination = string.IsNullOrWhiteSpace(windowToOpen.Location) ? _defaultLocation : windowToOpen.Location;
            var initialLocation = GetLocation(window);
            if (_shellPathComparer.IsEquivalent(destination, initialLocation))
            {
                SelectItems(window, windowToOpen.SelectedItems);
                WinApi.RestoreWindowToForeground(mainWindowHWnd);
                return true;
            }
            if (!_shellPathComparer.IsEquivalent(_defaultLocation, initialLocation)) return false;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            DWebBrowserEvents2_NavigateComplete2EventHandler navigateHandler = (object _, ref object _) =>
            {
                try
                {
                    if (!ReferenceEquals(_shellWindows, shell) || !_shellPathComparer.IsEquivalent(destination, GetLocation(window)))
                        return;
                    SelectItems(window, windowToOpen.SelectedItems);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException)
                {
                    tcs.TrySetResult(false);
                }
            };

            window.NavigateComplete2 += navigateHandler;
            try
            {
                Navigate(window, destination);
                if (excludedWindow == 0)
                    WinApi.RestoreWindowToForeground(mainWindowHWnd);
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task && tcs.Task.Result;
                if (completed && excludedWindow != 0)
                    WinApi.RestoreWindowToForeground(mainWindowHWnd);
                return completed;
            }
            finally
            {
                try
                {
                    window.NavigateComplete2 -= navigateHandler;
                }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException)
                {
                    Trace.TraceWarning($"Could not unhook tab navigation: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Trace.TraceWarning($"Could not open Explorer tab: {ex.Message}");
            return false;
        }
        finally
        {
            _toOpenWindowsLock.Release();
        }
    }
    private bool TryGetRecentlyClosedWindow(string location, out WindowRecord? closedWindow, int maxAge = 2_000)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(location);
            lock (_closedWindowsLock)
            {
                for (var i = _closedWindows.Count - 1; i >= 0; i--)
                {
                    var record = _closedWindows[i];
                    if (record.CreatedAt == 0 || _reopening.Contains(record) ||
                        unchecked((uint)(Environment.TickCount - (int)record.CreatedAt)) > maxAge) continue;
                    if (!_shellPathComparer.IsEquivalent(location, record.Location, targetPidl)) continue;
                    record.CreatedAt = 0;
                    closedWindow = record;
                    return true;
                }
            }
            closedWindow = null;
            return false;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }
    private nint GetMainWindowHWnd(nint otherThan)
    {
        if (_mainWindowHandle != otherThan && Helper.IsFileExplorerWindow(_mainWindowHandle))
            return _mainWindowHandle;

        var allWindows = WinApi.FindAllWindowsEx("CabinetWClass");

        // Get another handle other than the newly created one. (In case if it is still alive.)
        _mainWindowHandle = allWindows
            .Where(h => h != otherThan)
            .Reverse() // To get the last one in the z-index (the oldest)
            .OrderByDescending(h => WinApi.FindAllWindowsEx("ShellTabWindowClass", h).Count()) // The one with the most tabs first
            .FirstOrDefault();

        if (_mainWindowHandle != 0) return _mainWindowHandle;

        return Helper.IsFileExplorerWindow(otherThan) ? otherThan : 0;
    }
    private void GetTabHandle(InternetExplorer window)
    {
        if (_windowEntryDict.TryGetValue(window, out WindowEntry entry) && entry.OptionalKey is { } handle and > 0)
            return;

        if (window is not Interop.IServiceProvider sp) return;

        IShellBrowser? shellBrowser = null;
        try
        {
            sp.QueryService(ref _shellBrowserGuid, ref _shellBrowserGuid, out shellBrowser);
            if (shellBrowser == null) return;

            if (shellBrowser.GetWindow(out var hWnd) == 0 && hWnd != 0 &&
                _windowEntryDict.ContainsKey(window) && !_windowEntryDict.ContainsKey((nint?)hWnd))
                _windowEntryDict.UpdateOptionalKey(window, hWnd);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Trace.TraceWarning($"Could not identify Explorer tab: {ex.Message}");
        }
        finally
        {
            if (shellBrowser != null)
                Marshal.ReleaseComObject(shellBrowser);
        }
    }
    private static nint GetActiveTabHandle(nint windowHandle)
    {
        // Active tab always at the top of the z-index
        return WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
    }
    private InternetExplorer? GetWindowByTabHandle(nint tabHandle)
    {
        if (tabHandle == 0) return null;
        return _windowEntryDict.TryGetValue(tabHandle, out InternetExplorer? foundWindow) ? foundWindow : null;
    }
    private static string[]? GetSelectedItems(InternetExplorer window)
    {
        if (window.Document is not ShellFolderView document) return null;
        var selectedItems = document.SelectedItems();
        var count = selectedItems.Count;
        if (count == 0) return null;

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = selectedItems.Item(i).Name;
        }

        return result;
    }
    private static void SelectItems(InternetExplorer window, string[]? names)
    {
        if (names == null || names.Length == 0) return;

        if (window.Document is not ShellFolderView document) return;

        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            object item = document.Folder.ParseName(name);
            if (item == null) continue;
            document.SelectItem(ref item, 1);
        }
    }
    private static string GetLocation(InternetExplorer window)
    {
        var path = window.LocationURL;
        if (!string.IsNullOrWhiteSpace(path)) return Helper.NormalizeLocation(path);

        // Recycle Bin, This PC, etc
        return window.Document is ShellFolderView view && view.Folder is Folder2 folder
            ? Helper.NormalizeLocation(folder.Self.Path)
            : string.Empty;
    }
    private static void Navigate(InternetExplorer window, string path)
    {
        if (!path.Contains("#") && !path.Contains("%23"))
        {
            window.Navigate2(path);
            return;
        }

        var shell = new Shell();
        Folder? folder = null;
        try
        {
            folder = shell.NameSpace(path);
            window.Navigate2(folder);
        }
        finally
        {
            if (folder != null)
                Marshal.ReleaseComObject(folder);
            Marshal.ReleaseComObject(shell);
        }
    }
    private Task RunInStaThread(Action action, TaskCreationOptions tco = default, CancellationToken ct = default)
    {
        return Task.Factory.StartNew(action, ct, tco, _staTaskScheduler);
    }
    private void CheckForMainExplorer(object? sender, EventArgs args)
    {
        if (_disposed || _mainExplorerProcessId != 0) return;
        using var process = Helper.GetMainExplorerProcess();
        if (process == null) return;
            
        _mainExplorerProcessId = process.Id;
        try
        {
            InitializeShellObjects();
            _explorerCheckTimer.Stop();
            OnShellInitialized?.Invoke();
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Trace.TraceWarning($"Could not initialize Explorer: {ex.Message}");
            _mainExplorerProcessId = 0;
            DisposeShellObjects(persist: false);
        }
    }
    private void OnExplorerProcessTerminated(object? s, ProcessEventArgs e)
    {
        if (!_explorerCheckTimer.Dispatcher.CheckAccess())
        {
            _explorerCheckTimer.Dispatcher.BeginInvoke(() => OnExplorerProcessTerminated(s, e));
            return;
        }
        if (_disposed) return;

        // Main explorer.exe process (_shellWindows must be restarted)
        if (e.ProcessId == _mainExplorerProcessId)
        {
            _mainExplorerProcessId = 0;
            DisposeShellObjects();
            _pendingRestore = _lastSession;
            _restoreOffered = false;
            _explorerCheckTimer.Start();
            return;
        }
        
        // A separate Explorer process can own any of the tracked tabs.
        var lost = new List<WindowRecord>();
        foreach (var (window, info) in _windowEntryDict.ToArray<WindowEntry>())
        {
            try { _ = window.HWND; }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                if (SettingsManager.RestorePreviousWindows && info.Location is { } location)
                    lost.Add(CreateSessionRecord(info, location));
                RemoveWindowAndUnhookEvents(window, info);
            }
        }
        if (lost.Count == 0) return;
        _pendingRestore = _pendingRestore.Concat(lost).ToArray();
        _restoreOffered = false;
        PersistWindows();
    }

    private void InitializeShellObjects()
    {
        if (!_historyLoaded)
        {
            _lastHistory = SettingsManager.ClosedWindows;
            foreach (var record in _lastHistory) record.CreatedAt = 0;
            _closedWindows.AddRange(_lastHistory.Skip(Math.Max(0, _lastHistory.Length - HistoryLimit)));
            _lastSession = SettingsManager.OpenWindows;
            if (SettingsManager.RestorePreviousWindows) _pendingRestore = _lastSession;
            _historyLoaded = true;
        }

        _shellPathComparer = new ShellPathComparer();
        _shellWindows = new ShellWindows();

        _defaultLocation = Helper.GetDefaultExplorerLocation(_shellPathComparer);
        
        // Hook the global "WindowRegistered" event
        _windowRegisteredHandler = OnShellWindowRegistered;
        _shellWindows.WindowRegistered += _windowRegisteredHandler;

        // Hook the global "OBJECT_SHOW" event
        _eventObjectShowHookCallback = OnWindowShown;
        _eventObjectShowHookId = WinApi.SetWinEventHook(WinApi.EVENT_OBJECT_SHOW, WinApi.EVENT_OBJECT_SHOW, 0, _eventObjectShowHookCallback, 0, 0, 0);

        // Hook the event handlers for already-open windows
        var count = _shellWindows.Count;
        for (var i = 0; i < count; i++)
        {
            if (_shellWindows.Item(i) is not InternetExplorer window)
                continue;
            var windowInfo = new WindowInfo();
            _windowEntryDict.Add(window, windowInfo);
            window.PutProperty("seenBefore", true);

            GetTabHandle(window);
            HookWindowEvents(window, windowInfo);
        }

        _sessionTimer.Start();
    }

    private void DisposeShellObjects(bool persist = true)
    {
        var shell = _shellWindows;
        _sessionTimer.Stop();
        if (persist && shell != null)
            PersistWindows();
        _shellWindows = null!;

        if (_eventObjectShowHookId != 0)
            WinApi.UnhookWinEvent(_eventObjectShowHookId);
        _eventObjectShowHookId = 0;
        _eventObjectShowHookCallback = null;
        foreach (var hiddenWindow in _hiddenWindows.Keys.ToArray())
            RestoreHiddenWindow(hiddenWindow);

        if (shell != null && _windowRegisteredHandler != null)
        {
            try
            {
                shell.WindowRegistered -= _windowRegisteredHandler;
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                Trace.TraceWarning($"Could not unhook Explorer: {ex.Message}");
            }
        }
        _windowRegisteredHandler = null;

        foreach (var (window, windowInfo) in _windowEntryDict.ToArray<WindowEntry>())
            RemoveWindowAndUnhookEvents(window, windowInfo);
        if (shell != null)
            Marshal.ReleaseComObject(shell);
        _shellPathComparer?.Dispose();
        _shellPathComparer = null!;
        _mainWindowHandle = 0;
        _sessionGroups.Clear();
    }

    private Guid GetSessionGroup(nint windowHandle)
    {
        if (!_sessionGroups.TryGetValue(windowHandle, out var group))
            _sessionGroups[windowHandle] = group = Guid.NewGuid();
        return group;
    }

    private WindowRecord CreateSessionRecord(WindowInfo info, string location) =>
        new(location, info.WindowHandle, info.SelectedItems, info.Name ?? "")
        {
            SessionGroup = GetSessionGroup(info.WindowHandle)
        };

    private WindowRecord[] CaptureOpenWindows()
    {
        var records = new List<WindowRecord>();
        var windowHandles = new HashSet<nint>();
        foreach (var (window, info) in _windowEntryDict.ToArray<WindowEntry>())
        {
            if (info.OnNavigateHandler == null || info.Location is not { } location) continue;
            windowHandles.Add(info.WindowHandle);
            try { info.SelectedItems = GetSelectedItems(window); }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException) { }
            records.Add(CreateSessionRecord(info, location));
        }
        foreach (var handle in _sessionGroups.Keys.Where(handle => !windowHandles.Contains(handle)).ToArray())
            _sessionGroups.Remove(handle);
        return records.ToArray();
    }

    private void PersistWindows()
    {
        if (!_historyLoaded) return;

        WindowRecord[] closed;
        lock (_closedWindowsLock)
            closed = SettingsManager.SaveClosedHistory ? _closedWindows.ToArray() : [];

        var open = SettingsManager.RestorePreviousWindows ? CaptureOpenWindows().Concat(_pendingRestore).ToArray() : [];
        if (closed.SequenceEqual(_lastHistory) && open.Length == _lastSession.Length &&
            open.Zip(_lastSession, (current, previous) => current.SessionGroup == previous.SessionGroup &&
                current.Location == previous.Location && current.Name == previous.Name &&
                (current.SelectedItems ?? []).SequenceEqual(previous.SelectedItems ?? [])).All(equal => equal))
            return;

        _lastHistory = closed;
        _lastSession = open;
        SettingsManager.SaveWindows(closed, open);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _explorerCheckTimer.Stop();
        _processWatcher.ProcessTerminated -= OnExplorerProcessTerminated;
        _processWatcher.Dispose();
        DisposeShellObjects();
        _staTaskScheduler.Dispose();
        _instanceRunning = false;
        GC.SuppressFinalize(this);
    }
}
