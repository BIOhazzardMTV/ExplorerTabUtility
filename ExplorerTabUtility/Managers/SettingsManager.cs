using System;
using System.IO;
using System.Windows;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

public static class SettingsManager
{
    private static readonly object SaveLock = new();
    private static readonly SettingsFile Store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Constants.AppName,
        Constants.SettingsFileName));
    private static readonly Lazy<AppSettings> LoadedSettings = new(Store.Load);
    private static AppSettings Settings => LoadedSettings.Value;
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;
    internal static event Action<bool>? SaveStatusChanged;
    private static bool _savePending;

    internal static void Load() => _ = Settings;

    private static void NotifyStaticPropertyChanged([CallerMemberName] string propertyName = "")
    {
        StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(propertyName));
    }

    public static bool IsMouseHookActive
    {
        get => Settings.MouseHook;
        set
        {
            SaveSettings(() => Settings.MouseHook = value);
            NotifyStaticPropertyChanged();
        }
    }

    public static bool IsKeyboardHookActive
    {
        get => Settings.KeyboardHook;
        set
        {
            SaveSettings(() => Settings.KeyboardHook = value);
            NotifyStaticPropertyChanged();
        }
    }

    public static bool IsWindowHookActive
    {
        get => Settings.WindowHook;
        set
        {
            SaveSettings(() => Settings.WindowHook = value);
            NotifyStaticPropertyChanged();
        }
    }

    public static bool ReuseTabs
    {
        get => Settings.ReuseTabs;
        set
        {
            SaveSettings(() => Settings.ReuseTabs = value);
            NotifyStaticPropertyChanged();
        }
    }

    public static string HotKeyProfiles
    {
        get => Settings.HotKeyProfiles;
        set
        {
            lock (SaveLock)
            {
                var previous = Settings.HotKeyProfiles;
                Settings.HotKeyProfiles = value;
                try { WriteSettings(); }
                catch
                {
                    Settings.HotKeyProfiles = previous;
                    throw;
                }
            }
        }
    }

    public static Size FormSize
    {
        get => Settings.FormSize;
        set => SaveSettings(() => Settings.FormSize = value);
    }

    public static bool SaveProfilesOnExit
    {
        get => Settings.SaveProfilesOnExit;
        set => SaveSettings(() => Settings.SaveProfilesOnExit = value);
    }

    public static bool IsFirstRun
    {
        get => Settings.IsFirstRun;
        set => SaveSettings(() => Settings.IsFirstRun = value);
    }

    public static bool IsTrayIconHidden
    {
        get => Settings.IsTrayIconHidden;
        set => SaveSettings(() => Settings.IsTrayIconHidden = value);
    }

    public static bool HaveThemeIssue
    {
        get => Settings.HaveThemeIssue;
        set => SaveSettings(() => Settings.HaveThemeIssue = value);
    }

    public static bool AutoUpdate
    {
        get => Settings.AutoUpdate;
        set => SaveSettings(() => Settings.AutoUpdate = value);
    }

    public static bool SaveClosedHistory
    {
        get => Settings.SaveClosedWindows;
        set => SaveSettings(() => Settings.SaveClosedWindows = value);
    }

    public static bool RestorePreviousWindows
    {
        get => Settings.RestorePreviousWindows;
        set => SaveSettings(() => Settings.RestorePreviousWindows = value);
    }

    internal static WindowRecord[] ClosedWindows => Settings.ClosedWindows ?? [];
    internal static WindowRecord[] OpenWindows => Settings.OpenWindows ?? [];

    internal static void SaveWindows(WindowRecord[] closed, WindowRecord[] open) => SaveSettings(() =>
    {
        Settings.ClosedWindows = closed;
        Settings.OpenWindows = open;
    });

    internal static void ClearClosedWindows()
    {
        lock (SaveLock)
        {
            var previous = Settings.ClosedWindows;
            Settings.ClosedWindows = [];
            try
            {
                Store.Write(Settings, discardOldBackup: true);
                SetSavePending(false);
            }
            catch
            {
                Settings.ClosedWindows = previous;
                throw;
            }
        }
    }

    internal static void RetrySave()
    {
        if (_savePending) SaveSettings(static () => { });
    }

    private static void SaveSettings(Action update)
    {
        try
        {
            lock (SaveLock)
            {
                update();
                WriteSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError($"Failed to save settings: {ex.Message}");
        }
    }

    private static void WriteSettings()
    {
        try { Store.Write(Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetSavePending(true);
            throw;
        }
        SetSavePending(false);
    }

    private static void SetSavePending(bool value)
    {
        if (_savePending == value) return;
        _savePending = value;
        SaveStatusChanged?.Invoke(value);
    }

}

internal sealed class SettingsFile(string path)
{
    private readonly string _backupPath = path + ".bak";
    private bool _canBackUp;

    public AppSettings Load()
    {
        var primary = Read(path);
        _canBackUp = primary != null;
        var settings = primary ?? Read(_backupPath) ?? new AppSettings();
        // Older releases stored the open session in the closed history.
        var history = settings.ClosedWindows ?? [];
        settings.OpenWindows = Array.FindAll(settings.OpenWindows ?? Array.FindAll(history, w => w is { Restore: true }),
            w => w is { Location: not null });
        settings.ClosedWindows = Array.FindAll(history, w => w is { Restore: false, Location: not null });
        return settings;
    }

    private static AppSettings? Read(string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file));
            }
            catch (IOException ex) when (attempt < 3 && (ex.HResult & 0xffff) is 32 or 33)
            {
                System.Threading.Thread.Sleep(100);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or OverflowException)
            {
                System.Diagnostics.Trace.TraceWarning($"Invalid settings in {file}: {ex.Message}");
                return null;
            }
        }
    }

    public void Write(AppSettings settings, bool discardOldBackup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings);
                stream.Flush(flushToDisk: true);
            }

            // A history clear must also remove the old paths from the recovery copy.
            if (discardOldBackup)
            {
                var cleanBackup = _backupPath + ".tmp";
                try
                {
                    File.Copy(tempPath, cleanBackup, overwrite: true);
                    if (File.Exists(_backupPath))
                        File.Replace(cleanBackup, _backupPath, null);
                    else
                        File.Move(cleanBackup, _backupPath);
                }
                finally { File.Delete(cleanBackup); }
            }
            // Keep the valid backup if startup had to recover from a damaged primary.
            if (File.Exists(path))
                File.Replace(tempPath, path, _canBackUp && !discardOldBackup ? _backupPath : null);
            else
                File.Move(tempPath, path);

            _canBackUp = true;
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}

internal class AppSettings
{
    public bool MouseHook { get; set; }
    public bool KeyboardHook { get; set; } = true;
    public bool WindowHook { get; set; } = true;
    public bool ReuseTabs { get; set; } = true;
    public Size FormSize { get; set; } = new(852, 402);
    public bool SaveProfilesOnExit { get; set; } = true;
    public bool IsFirstRun { get; set; } = true;
    public bool IsTrayIconHidden { get; set; }
    public bool HaveThemeIssue { get; set; }
    public bool AutoUpdate { get; set; }
    public string HotKeyProfiles { get; set; } = Constants.DefaultHotKeyProfiles;
    public bool SaveClosedWindows { get; set; }
    public bool RestorePreviousWindows { get; set; }
    public WindowRecord[]? ClosedWindows { get; set; }
    public WindowRecord[]? OpenWindows { get; set; }
}
