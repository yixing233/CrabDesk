using System.Text.Json;

namespace CrabDesk.Core;

public sealed class JsonLayoutStore : ILayoutStore
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public JsonLayoutStore(string? rootDirectory = null)
    {
        var root = rootDirectory
            ?? Environment.GetEnvironmentVariable("CRABDESK_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrabDesk");
        Directory.CreateDirectory(root);
        StatePath = Path.Combine(root, "config.json");
    }

    public string StatePath { get; }

    public async Task<CrabDeskState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var state = await TryReadAsync(StatePath, cancellationToken).ConfigureAwait(false)
            ?? await TryReadAsync(StatePath + ".bak", cancellationToken).ConfigureAwait(false)
            ?? CreateDefaultState();

        NormalizeState(state);
        return state;
    }

    public async Task SaveAsync(CrabDeskState state, CancellationToken cancellationToken = default)
    {
        NormalizeState(state);
        var tempPath = StatePath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(StatePath))
        {
            File.Copy(StatePath, StatePath + ".bak", true);
        }

        File.Move(tempPath, StatePath, true);
    }

    public static CrabDeskState CreateDefaultState(string monitorId = "primary") => new();

    private static async Task<CrabDeskState?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            return await JsonSerializer.DeserializeAsync<CrabDeskState>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static void NormalizeState(CrabDeskState state)
    {
        var previousVersion = state.SchemaVersion;
        state.SchemaVersion = 15;
        state.Settings ??= new AppSettings();
        state.Settings.DesktopBehavior ??= new DesktopBehaviorSettings();
        state.Settings.Appearance ??= new GlobalAppearanceSettings();
        state.Settings.Backup ??= new BackupSettings();
        state.Settings.Hotkeys ??= new HotkeySettings();
        state.Settings.Updates ??= new UpdateSettings();
        if (!Enum.IsDefined(state.Settings.Updates.Channel))
        {
            state.Settings.Updates.Channel = UpdateChannel.Stable;
        }
        state.Settings.Updates.CachedETag ??= string.Empty;
        if (!Enum.IsDefined(state.Settings.Updates.LastStatus))
        {
            state.Settings.Updates.LastStatus = UpdateCheckStatus.NotChecked;
        }
        state.Settings.Updates.LastMessage ??= string.Empty;
        state.Settings.Updates.LatestKnownVersion ??= string.Empty;
        state.Settings.Updates.CachedReleaseName ??= string.Empty;
        state.Settings.Updates.CachedReleaseNotes ??= string.Empty;
        state.Settings.Updates.CachedReleasePageUrl ??= string.Empty;
        state.Settings.Updates.CachedInstallerUrl ??= string.Empty;
        state.Settings.Updates.CachedSha256Url ??= string.Empty;
        state.Settings.Updates.RepositoryOwner = state.Settings.Updates.RepositoryOwner?.Trim() ?? string.Empty;
        state.Settings.Updates.RepositoryName = state.Settings.Updates.RepositoryName?.Trim() ?? string.Empty;
        state.Settings.Hotkeys.ShowDesktop ??= new HotkeyBinding
        {
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
            Key = HotkeyKey.D
        };
        state.Settings.Hotkeys.OrganizeDesktop ??= new HotkeyBinding
        {
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
            Key = HotkeyKey.O
        };
        NormalizeHotkey(state.Settings.Hotkeys.ShowDesktop, HotkeyKey.D);
        NormalizeHotkey(state.Settings.Hotkeys.OrganizeDesktop, HotkeyKey.O);
        state.Settings.Backup.RetentionDays = Math.Clamp(state.Settings.Backup.RetentionDays, 1, 365);
        state.Settings.Appearance.CornerRadius = Math.Clamp(state.Settings.Appearance.CornerRadius, 0, 20);
        state.Settings.Appearance.IconHorizontalSpacing = Math.Clamp(
            state.Settings.Appearance.IconHorizontalSpacing,
            56,
            160);
        state.Settings.Appearance.IconVerticalSpacing = Math.Clamp(
            state.Settings.Appearance.IconVerticalSpacing,
            56,
            180);
        if (string.IsNullOrWhiteSpace(state.Settings.Appearance.SelectionColor))
        {
            state.Settings.Appearance.SelectionColor = "#FF4A5BB1";
        }
        state.Boxes ??= [];
        state.Assignments = new Dictionary<string, Guid>(state.Assignments ?? [], StringComparer.OrdinalIgnoreCase);
        state.Organization ??= new OrganizationSettings();
        state.OrganizationRules ??= [];

        if (previousVersion < 15)
        {
            // Older desktop surfaces could trap Explorer input. Upgrades restart in fail-open mode.
            state.Settings.TakeOverDesktop = false;
        }

        if (previousVersion < 14)
        {
            state.Settings.DesktopBehavior.ShowDesktopContextMenu = true;
            if (state.Assignments.Count == 0 && state.OrganizationRules.Count == 0 &&
                state.Boxes.Count == 2 &&
                state.Boxes.Any(box => box.Title == "常用" && box.Bounds == new LayoutRect(36, 52, 520, 420)) &&
                state.Boxes.Any(box => box.Title == "工作" && box.Bounds == new LayoutRect(584, 52, 360, 250)))
            {
                state.Boxes.Clear();
            }
        }

        foreach (var (rule, index) in state.OrganizationRules.Select((rule, index) => (rule, index)))
        {
            rule.Title = string.IsNullOrWhiteSpace(rule.Title) ? "未命名规则" : rule.Title.Trim();
            rule.NamePattern = string.IsNullOrWhiteSpace(rule.NamePattern) ? "*" : rule.NamePattern.Trim();
            rule.ItemKinds ??= [];
            rule.Extensions = (rule.Extensions ?? [])
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Select(NormalizeExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            rule.Priority = rule.Priority < 0 ? index : rule.Priority;
        }

        if (previousVersion < 14)
        {
            var builtInTitles = new HashSet<string>(
                ["快捷方式", "目录", "文档", "图片", "压缩包"],
                StringComparer.CurrentCultureIgnoreCase);
            foreach (var rule in state.OrganizationRules.Where(rule => builtInTitles.Contains(rule.Title)).ToArray())
            {
                var box = rule.TargetBoxId is { } target
                    ? state.Boxes.FirstOrDefault(candidate => candidate.Id == target &&
                        string.Equals(candidate.Title, rule.Title, StringComparison.CurrentCultureIgnoreCase))
                    : null;
                if (box is null)
                {
                    continue;
                }
                box.IsAutoGenerated = true;
                if (!state.Assignments.Values.Contains(box.Id))
                {
                    state.Boxes.Remove(box);
                    state.OrganizationRules.Remove(rule);
                }
            }
        }

        if (previousVersion < 2)
        {
            state.MigratedFromLegacyIconTakeover = true;
            state.Assignments.Clear();
            foreach (var box in state.Boxes)
            {
                box.IsSystemBox = false;
                if (box.Title == "未分类")
                {
                    box.Title = "常用";
                }
            }
        }

        if (state.Boxes.Count == 0)
        {
            state.Boxes.AddRange(CreateDefaultState().Boxes);
        }

        foreach (var box in state.Boxes)
        {
            box.Title = string.IsNullOrWhiteSpace(box.Title) ? "未命名盒子" : box.Title.Trim();
            box.Appearance ??= new BoxAppearance();
            if (previousVersion < 12 && Math.Abs(box.Appearance.Opacity - 0.88) < 0.001)
            {
                box.Appearance.Opacity = 1;
            }
            if (previousVersion < 12 && box.Appearance.Background.StartsWith("#D9", StringComparison.OrdinalIgnoreCase))
            {
                box.Appearance.Background = "#FF" + box.Appearance.Background[3..];
            }
            box.Appearance.Opacity = Math.Clamp(box.Appearance.Opacity, 0.35, 1);
            box.Appearance.IconSize = Math.Clamp(box.Appearance.IconSize, 24, 96);
            box.Appearance.LabelFontSize = Math.Clamp(box.Appearance.LabelFontSize, 8, 16);
            box.Appearance.TitleBarHeight = Math.Clamp(box.Appearance.TitleBarHeight, 32, 56);
            box.Appearance.TitleFontSize = Math.Clamp(box.Appearance.TitleFontSize, 8, 20);
            if (string.IsNullOrWhiteSpace(box.Appearance.Background))
            {
                box.Appearance.Background = "#FF2A2D32";
            }
            if (string.IsNullOrWhiteSpace(box.Appearance.Accent))
            {
                box.Appearance.Accent = "#FF4EA1D3";
            }
            if (string.IsNullOrWhiteSpace(box.Appearance.TitleColor))
            {
                box.Appearance.TitleColor = "Auto";
            }
            box.ItemOrder ??= [];
            if (box.MappedFolder is not null)
            {
                box.MappedFolder.Path = string.IsNullOrWhiteSpace(box.MappedFolder.Path)
                    ? string.Empty
                    : Environment.ExpandEnvironmentVariables(box.MappedFolder.Path.Trim());
            }
        }

        var validBoxIds = state.Boxes.Where(box => !box.IsMappedFolder).Select(box => box.Id).ToHashSet();
        foreach (var key in state.Assignments.Where(pair => !validBoxIds.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
        {
            state.Assignments.Remove(key);
        }
    }

    private static string NormalizeExtension(string value)
    {
        var extension = value.Trim();
        return extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    }

    private static void NormalizeHotkey(HotkeyBinding binding, HotkeyKey defaultKey)
    {
        const HotkeyModifiers allowed = HotkeyModifiers.Alt |
            HotkeyModifiers.Control |
            HotkeyModifiers.Shift |
            HotkeyModifiers.Windows;
        binding.Modifiers &= allowed;
        if (binding.Modifiers == HotkeyModifiers.None)
        {
            binding.Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;
        }
        if (!Enum.IsDefined(binding.Key))
        {
            binding.Key = defaultKey;
        }
    }
}
