using System;
using System.Linq;
using System.Text.Json;
using System.Windows.Controls;
using System.Collections.Generic;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.UI.Views;

namespace ExplorerTabUtility.Managers;

public class ProfileManager
{
    // Saved state (persistent)
    private volatile HotKeyProfile[] _savedProfiles;

    // Temporary state (for editing)
    private readonly List<HotKeyProfile> _tempProfiles = [];
    private readonly Panel _profilePanel;

    public event Action? KeybindingsHookStarted;
    public event Action? KeybindingsHookStopped;

    public ProfileManager(Panel profilePanel, string savedProfiles)
    {
        _profilePanel = profilePanel;

        var profiles = ReadProfiles(savedProfiles) ?? ReadProfiles(Constants.DefaultHotKeyProfiles)!;
        _savedProfiles = profiles.ToArray();
        _tempProfiles.AddRange(profiles.Select(profile => profile.Clone()));
        RefreshPanel();
    }

    private static List<HotKeyProfile>? ReadProfiles(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var profiles = JsonSerializer.Deserialize<List<HotKeyProfile>>(json);
            return profiles != null && profiles.All(profile => profile != null &&
                Enum.IsDefined(typeof(HotkeyScope), profile.Scope) &&
                Enum.IsDefined(typeof(HotKeyAction), profile.Action)) ? profiles : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void AddProfile(HotKeyProfile? profile = null)
    {
        var newProfile = profile?.Clone() ?? new HotKeyProfile();
        _tempProfiles.Add(newProfile);
        _profilePanel.Children.Add(new HotKeyProfileControl(newProfile, RemoveProfile, KeybindingHookStarted, KeybindingHookStopped));
    }

    private void RemoveProfile(HotKeyProfile profile)
    {
        _tempProfiles.Remove(profile);
        var control = FindControlByProfile(profile);
        if (control != null)
            _profilePanel.Children.Remove(control);
    }

    private void RefreshPanel()
    {
        _profilePanel.Children.Clear();

        foreach (var profile in _tempProfiles)
        {
            _profilePanel.Children.Add(new HotKeyProfileControl(profile, RemoveProfile, KeybindingHookStarted, KeybindingHookStopped));
        }
    }

    private void KeybindingHookStarted() => KeybindingsHookStarted?.Invoke();
    private void KeybindingHookStopped() => KeybindingsHookStopped?.Invoke();

    public void SetProfileEnabledFromTray(HotKeyProfile profile, bool enabled)
    {
        // Find and update in saved profiles (for tray menu)
        var savedProfile = _savedProfiles.First(p => p.Id == profile.Id);
        savedProfile.IsEnabled = enabled;

        // Find and update in temp profiles (for panel)
        var tempProfile = _tempProfiles.FirstOrDefault(p => p.Id == profile.Id);
        if (tempProfile == null) return;

        tempProfile.IsEnabled = enabled;
        var control = FindControlByProfile(tempProfile);
        if (control != null) control.IsEnabled = enabled;
    }

    public IReadOnlyList<HotKeyProfile> GetProfiles() => _savedProfiles;
    public IEnumerable<HotKeyProfile> GetKeyboardProfiles() => _savedProfiles.Where(p => !p.IsMouse);
    public IEnumerable<HotKeyProfile> GetMouseProfiles() => _savedProfiles.Where(p => p.IsMouse);

    public void SaveProfiles(Action<string> persist)
    {
        var profiles = _tempProfiles.Where(p => p.HotKeys is { Length: > 0 }).Select(p => p.Clone()).ToArray();
        persist(JsonSerializer.Serialize(profiles));

        // Remove profiles that don't have any hotkeys
        _tempProfiles.Where(p => p.HotKeys == null || p.HotKeys.Length == 0).ToList().ForEach(RemoveProfile);

        _savedProfiles = profiles;
    }

    public bool ImportProfiles(string jsonString)
    {
        var profiles = ReadProfiles(jsonString);
        if (profiles == null) return false;

        _tempProfiles.Clear();
        _tempProfiles.AddRange(profiles);
        RefreshPanel();
        return true;
    }

    public string ExportProfiles() => JsonSerializer.Serialize(_savedProfiles);

    private HotKeyProfileControl? FindControlByProfile(HotKeyProfile profile)
    {
        return _profilePanel.Children
            .OfType<HotKeyProfileControl>()
            .FirstOrDefault(c => c.Tag?.Equals(profile) == true);
    }
}
