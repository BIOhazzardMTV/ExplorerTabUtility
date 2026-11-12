using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ExplorerTabUtility.Hooks;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.UI.Views;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExplorerTabUtility-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            LockedSettingsKeepLatestVersion(path);
            RejectedImportKeepsDraft(path);
            DisposeBeforeExplorerStarts();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static void LockedSettingsKeepLatestVersion(string path)
    {
        var store = new SettingsFile(path);
        var settings = store.Load();
        store.Write(settings);
        settings.HaveThemeIssue = true;
        store.Write(settings);

        var reader = new SettingsFile(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                reader.Load();
                throw new InvalidOperationException("A locked primary must never be mistaken for a damaged file.");
            }
            catch (IOException)
            {
                // The caller can retry once the sharing violation clears.
            }
        }

        settings = reader.Load();
        Check(settings.HaveThemeIssue, "Reload must use the latest primary after the lock clears.");
        settings.SaveProfilesOnExit = false;
        reader.Write(settings);
        Check(new SettingsFile(path).Load().HaveThemeIssue, "Writing another preference must preserve the latest value.");

        File.WriteAllText(path, "{");
        Check(new SettingsFile(path).Load().HaveThemeIssue, "A damaged primary must still recover from the backup.");
        Console.WriteLine("PASS locked settings and backup recovery");
    }

    private static void RejectedImportKeepsDraft(string path)
    {
        var app = new Application();
        foreach (var dictionary in new[] { "Colors", "DefaultStyles", "ThemeResources" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/ExplorerTabUtility;component/UI/Themes/{dictionary}.xaml")
            });

        const string original = """[{"Name":"First","HotKeys":[91,69]},{"Name":"Second","HotKeys":[17,68]}]""";
        var panel = new StackPanel();
        var manager = new ProfileManager(panel, original);
        var profile = (HotKeyProfile)panel.Children[0].GetValue(FrameworkElement.TagProperty);
        profile.Name = "Unsaved edit";
        Check(!manager.ImportProfiles("""[{"Name":"Partial","HotKeys":[17,70]},null]"""), "An invalid import must be rejected.");
        Check(ReferenceEquals(profile, ((HotKeyProfileControl)panel.Children[0]).Tag), "A rejected import must leave the existing editor intact.");

        var store = new SettingsFile(path);
        var settings = store.Load();
        manager.SaveProfiles(json =>
        {
            settings.HotKeyProfiles = json;
            store.Write(settings);
        });
        var saved = JsonSerializer.Deserialize<HotKeyProfile[]>(new SettingsFile(path).Load().HotKeyProfiles)!;
        Check(saved.Length == 2 && saved[0].Name == "Unsaved edit" && saved[1].Name == "Second",
            "SAVE must preserve both original profiles and the pending edit after a rejected import.");
        Console.WriteLine("PASS rejected profile import");
    }

    private static void DisposeBeforeExplorerStarts()
    {
        var watcher = new ExplorerWatcher();
        watcher.Dispose();
        watcher.Dispose();
        Console.WriteLine("PASS repeated disposal before Explorer starts");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
