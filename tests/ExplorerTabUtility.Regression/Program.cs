using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using ExplorerTabUtility.Helpers;
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
            SessionSurvivesHistoryClearAndBackupRecovery(path);
            RejectedImportKeepsDraft(path);
            LocationNormalizationPreservesPathRoots();
            ReleaseCheckRequiresMatchingForkAsset();
            DisposeBeforeExplorerStarts();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static void SessionSurvivesHistoryClearAndBackupRecovery(string path)
    {
        File.WriteAllText(path, """{"ClosedWindows":[{"Location":"C:\\Closed"},{"Location":"C:\\Open","Restore":true}]}""");
        var store = new SettingsFile(path);
        var settings = store.Load();
        Check(settings.ClosedWindows?.Single().Location == @"C:\Closed", "A legacy history entry must remain in history.");
        Check(settings.OpenWindows?.Single().Location == @"C:\Open", "A legacy restore entry must migrate to the session.");

        var firstGroup = Guid.NewGuid();
        var secondGroup = Guid.NewGuid();
        settings.OpenWindows =
        [
            new(@"C:\Open") { SessionGroup = firstGroup },
            new(@"C:\Open") { SessionGroup = firstGroup },
            new(@"C:\Open") { SessionGroup = secondGroup }
        ];
        store.Write(settings);
        settings.ClosedWindows = [];
        store.Write(settings, discardOldBackup: true);
        Check(new SettingsFile(path).Load().ClosedWindows?.Length == 0, "History must be cleared immediately on disk.");

        File.WriteAllText(path, "{");
        var recovered = new SettingsFile(path).Load();
        Check(recovered.ClosedWindows?.Length == 0, "Backup recovery must keep history empty.");
        Check(recovered.OpenWindows?.Select(w => w.SessionGroup).SequenceEqual(new[] { firstGroup, firstGroup, secondGroup }) == true,
            "Recovery must preserve duplicate tabs and their window groups.");
        store.Load();
        store.Write(recovered);
        Console.WriteLine("PASS session migration and history clear with backup recovery");
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
        Check(!manager.ImportProfiles("""[{"HotKeys":[17,70],"Action":"UnknownAction"}]"""), "Unknown action names must be rejected.");
        Check(!manager.ImportProfiles("""[{"HotKeys":[17,70],"Action":999}]"""), "Unknown action numbers must be rejected.");
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

    private static void LocationNormalizationPreservesPathRoots()
    {
        Check(Helper.NormalizeLocation("https://example.org/a?x=1") == "https://example.org/a?x=1", "An HTTPS URL must keep its slashes.");
        Check(Helper.NormalizeLocation(@"\\server\share") == @"\\server\share", "A UNC path must keep its prefix.");
        Check(Helper.NormalizeLocation(@"C:\") == @"C:\", "A drive root must remain absolute.");
        Check(Helper.NormalizeLocation("file:///C:/My%20Files") == @"C:\My Files", "A Shell file URL must resolve to its local path.");
        Console.WriteLine("PASS path normalization");
    }

    private static void ReleaseCheckRequiresMatchingForkAsset()
    {
        const string tag = "v9.8.7";
#if NETFRAMEWORK
        const string flavor = "NetFW4.8.1";
#else
        const string flavor = "Net10.0_FrameworkDependent";
#endif
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var name = $"ExplorerTabUtility_{tag}_{arch}_{flavor}.zip";
        var url = $"https://github.com/BIOhazzardMTV/ExplorerTabUtility/releases/download/{tag}/{name}";
        var hash = new string('a', 64);
        var asset = new JsonObject { ["name"] = name, ["browser_download_url"] = url, ["digest"] = "sha256:" + hash };
        var release = new JsonObject { ["tag_name"] = tag, ["assets"] = new JsonArray(asset) };

        var info = UpdateManager.ReadRelease(release);
        Check(info?.DownloadURL == url && info.CheckSum?.Value == hash, "The matching fork archive must carry its SHA-256.");
        asset["browser_download_url"] = url.Replace("BIOhazzardMTV", "w4po");
        Check(UpdateManager.ReadRelease(release) == null, "The upstream archive must be ignored.");
        asset["browser_download_url"] = url;
        asset["digest"] = null;
        Check(UpdateManager.ReadRelease(release) == null, "An archive without SHA-256 must be ignored.");
        Console.WriteLine("PASS fork update and archive verification metadata");
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
