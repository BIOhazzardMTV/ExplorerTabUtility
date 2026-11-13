using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using AutoUpdaterDotNET;
using AutoUpdaterDotNET.Markdown;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

internal static class UpdateManager
{
    private static bool _initialized;

    public static void CheckForUpdates()
    {
        if (!_initialized)
        {
            AutoUpdater.FlattenRootFolder = true;
            using var icon = Helper.GetIcon();
            AutoUpdater.Icon = icon?.ToBitmap();
            AutoUpdater.ChangelogViewerProvider = new MarkdownViewerProvider();
            AutoUpdater.ParseUpdateInfoEvent += ParseUpdateInfo;
            _initialized = true;
        }
        AutoUpdater.Start(Constants.UpdateUrl);
    }

    private static void ParseUpdateInfo(ParseUpdateInfoEventArgs p)
    {
        try
        {
            p.UpdateInfo = ReadRelease(JsonSerializer.Deserialize<JsonNode>(p.RemoteData));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex.Message}");
        }
    }

    internal static UpdateInfoEventArgs? ReadRelease(JsonNode? jsonNode)
    {
        if (jsonNode?["tag_name"]?.GetValue<string>() is not { } tag ||
            FindMatchingUpdateAsset(jsonNode, tag) is not { } asset) return null;
        return new UpdateInfoEventArgs
        {
            CurrentVersion = tag.Substring(1),
            ChangelogText = jsonNode["body"]?.GetValue<string>() ?? "",
            ChangelogURL = $"https://github.com/{Constants.ReleaseRepository}/releases/tag/{tag}",
            DownloadURL = asset.Url,
            CheckSum = new CheckSum { Value = asset.Hash, HashingAlgorithm = "SHA256" }
        };
    }

    private static (string Url, string Hash)? FindMatchingUpdateAsset(JsonNode jsonNode, string tag)
    {
        if (tag.Length < 2 || tag[0] != 'v' || !Version.TryParse(tag.Substring(1), out _)) return null;
#if NETFRAMEWORK
        const string flavor = "NetFW4.8.1";
#else
        const string flavor = "Net10.0_FrameworkDependent";
#endif
        var currentArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => null
        };
        if (currentArch == null) return null;

        var name = $"ExplorerTabUtility_{tag}_{currentArch}_{flavor}.zip";
        var url = $"https://github.com/{Constants.ReleaseRepository}/releases/download/{tag}/{name}";
        foreach (var asset in jsonNode["assets"]!.AsArray())
        {
            if (asset?["name"]?.GetValue<string>() != name ||
                asset["browser_download_url"]?.GetValue<string>() != url) continue;
            var digest = asset["digest"]?.GetValue<string>();
            if (digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.IsMatch(digest.Substring(7), @"\A[0-9a-fA-F]{64}\z"))
                return (url, digest.Substring(7));
        }

        return null;
    }
}
