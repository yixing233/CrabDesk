using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CrabDesk.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StateIsSavedAndLoadedAtomically()
    {
        var store = new JsonLayoutStore(_root);
        var state = JsonLayoutStore.CreateDefaultState("display-1");
        state.Boxes.Add(new DesktopBox { MonitorId = "display-1" });
        state.Boxes[0].Title = "工作";
        state.Boxes[0].Appearance.TitleAlignment = BoxTitleAlignment.Center;
        state.Boxes[0].Appearance.TitleColor = "#FF21A179";
        state.Boxes[0].Appearance.TitleFontFamily = "Microsoft YaHei UI";
        state.Boxes[0].Appearance.TitleFontSize = 15;
        state.Boxes[0].Appearance.TitleFontBold = false;
        state.Boxes[0].Appearance.ShowCollapseButton = false;
        state.Boxes[0].Appearance.Material = BoxMaterialKind.Solid;
        state.Boxes[0].Appearance.Background = "#FF162A3A";
        state.Boxes[0].Appearance.Accent = "#FF31A86D";
        state.Boxes[0].Appearance.Opacity = 0.6;
        state.Boxes[0].Appearance.TitleBarHeight = 80;
        state.Boxes[0].Appearance.LabelFontFamily = "Consolas";
        state.Boxes[0].Appearance.LabelFontSize = 20;
        state.Boxes[0].Appearance.ShowItemLabels = false;
        state.Boxes[0].ExpandOnHover = true;
        state.Assignments["file:123"] = state.Boxes[0].Id;
        state.Settings.ThemeMode = ApplicationThemeMode.Dark;
        state.Settings.WindowBackdrop = "Acrylic";
        state.Settings.Appearance.CornerRadius = 42;
        state.Settings.Hotkeys.ShowDesktop = new HotkeyBinding
        {
            Enabled = true,
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
            Key = HotkeyKey.F9
        };
        state.Settings.DesktopBehavior.ToggleIconsOnDesktopDoubleClick = true;
        state.Settings.DesktopBehavior.ExpandBoxOnHover = true;
        state.Settings.DesktopBehavior.RefreshAfterRename = false;
        state.Settings.Appearance.HoverFeedback = false;
        state.Settings.Appearance.AnimationEnabled = false;
        state.Settings.Updates.CheckOnStartup = false;
        state.Settings.Updates.Channel = UpdateChannel.Preview;
        state.Settings.Updates.CachedETag = "\"release-etag\"";
        state.Settings.Updates.LatestKnownVersion = "0.6.1-beta.1";
        state.Settings.Updates.LastStatus = UpdateCheckStatus.UpdateAvailable;
        state.Settings.Updates.LastMessage = "cached";
        state.OrganizationRules.Add(new OrganizationRule
        {
            Title = "  文档  ",
            Extensions = ["PDF", ".pdf"],
            TargetBoxId = state.Boxes[0].Id
        });

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.Equal("工作", loaded.Boxes[0].Title);
        Assert.Equal(BoxTitleAlignment.Center, loaded.Boxes[0].Appearance.TitleAlignment);
        Assert.Equal("#FF21A179", loaded.Boxes[0].Appearance.TitleColor);
        Assert.Equal("Microsoft YaHei UI", loaded.Boxes[0].Appearance.TitleFontFamily);
        Assert.Equal(15, loaded.Boxes[0].Appearance.TitleFontSize);
        Assert.False(loaded.Boxes[0].Appearance.TitleFontBold);
        Assert.False(loaded.Boxes[0].Appearance.ShowCollapseButton);
        Assert.Equal(BoxMaterialKind.Solid, loaded.Boxes[0].Appearance.Material);
        Assert.Equal("#FF162A3A", loaded.Boxes[0].Appearance.Background);
        Assert.Equal("#FF31A86D", loaded.Boxes[0].Appearance.Accent);
        Assert.Equal(0.6, loaded.Boxes[0].Appearance.Opacity);
        Assert.Equal(56, loaded.Boxes[0].Appearance.TitleBarHeight);
        Assert.Equal("Consolas", loaded.Boxes[0].Appearance.LabelFontFamily);
        Assert.Equal(16, loaded.Boxes[0].Appearance.LabelFontSize);
        Assert.False(loaded.Boxes[0].Appearance.ShowItemLabels);
        Assert.True(loaded.Boxes[0].ExpandOnHover);
        Assert.Equal(state.Boxes[0].Id, loaded.Assignments["file:123"]);
        Assert.Equal(ApplicationThemeMode.Dark, loaded.Settings.ThemeMode);
        Assert.Equal("Acrylic", loaded.Settings.WindowBackdrop);
        Assert.Equal(20, loaded.Settings.Appearance.CornerRadius);
        Assert.True(loaded.Settings.Hotkeys.ShowDesktop.Enabled);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, loaded.Settings.Hotkeys.ShowDesktop.Modifiers);
        Assert.Equal(HotkeyKey.F9, loaded.Settings.Hotkeys.ShowDesktop.Key);
        Assert.True(loaded.Settings.DesktopBehavior.ToggleIconsOnDesktopDoubleClick);
        Assert.True(loaded.Settings.DesktopBehavior.ExpandBoxOnHover);
        Assert.False(loaded.Settings.DesktopBehavior.RefreshAfterRename);
        Assert.False(loaded.Settings.Appearance.HoverFeedback);
        Assert.False(loaded.Settings.Appearance.AnimationEnabled);
        Assert.False(loaded.Settings.Updates.CheckOnStartup);
        Assert.Equal(UpdateChannel.Preview, loaded.Settings.Updates.Channel);
        Assert.Equal("\"release-etag\"", loaded.Settings.Updates.CachedETag);
        Assert.Equal("0.6.1-beta.1", loaded.Settings.Updates.LatestKnownVersion);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, loaded.Settings.Updates.LastStatus);
        Assert.Equal("cached", loaded.Settings.Updates.LastMessage);
        var customRule = Assert.Single(loaded.OrganizationRules.Where(rule =>
            string.IsNullOrEmpty(rule.BuiltInId)));
        Assert.Equal("文档", customRule.Title);
        Assert.Equal([".pdf"], customRule.Extensions);
        Assert.True(File.Exists(store.StatePath));
    }

    [Fact]
    public void DefaultStateContainsBuiltInOrganizationRules()
    {
        var rules = JsonLayoutStore.CreateDefaultState().OrganizationRules
            .OrderBy(rule => rule.Priority)
            .ToArray();

        Assert.Equal(5, rules.Length);
        Assert.Equal(["目录", "文档", "图片", "压缩", "其它"], rules.Select(rule => rule.Title));
        Assert.Equal(5, rules.Select(rule => rule.BuiltInId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.BuiltInId)));
        Assert.Equal(BuiltInOrganizationRules.OtherId, rules[^1].BuiltInId);
        Assert.Equal([".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz"],
            rules.Single(rule => rule.BuiltInId == BuiltInOrganizationRules.ArchivesId).Extensions);
    }

    [Fact]
    public async Task VersionSixteenAddsBuiltInsOnceWithoutChangingUserRule()
    {
        Directory.CreateDirectory(_root);
        var customRuleId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), $$"""
        {
          "SchemaVersion": 16,
          "Boxes": [],
          "Assignments": {},
          "OrganizationRules": [{
            "Id": "{{customRuleId}}",
            "Title": "文档",
            "Priority": 7,
            "ItemKinds": [0],
            "NamePattern": "Report-*",
            "Extensions": ["custom"]
          }]
        }
        """);
        var store = new JsonLayoutStore(_root);

        var first = await store.LoadAsync();
        await store.SaveAsync(first);
        var second = await store.LoadAsync();

        var custom = Assert.Single(second.OrganizationRules.Where(rule => rule.Id == customRuleId));
        Assert.Equal("文档", custom.Title);
        Assert.Equal(7, custom.Priority);
        Assert.Equal("Report-*", custom.NamePattern);
        Assert.Equal([".custom"], custom.Extensions);
        Assert.Equal(5, second.OrganizationRules.Count(rule => !string.IsNullOrWhiteSpace(rule.BuiltInId)));
        Assert.Equal(5, second.OrganizationRules.Select(rule => rule.BuiltInId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());
    }

    [Fact]
    public async Task VersionSixteenAdoptsUneditedGeneratedRuleWithoutDuplicatingIt()
    {
        Directory.CreateDirectory(_root);
        var boxId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), $$"""
        {
          "SchemaVersion": 16,
          "Boxes": [{
            "Id": "{{boxId}}",
            "Title": "文档",
            "MonitorId": "primary",
            "IsAutoGenerated": true
          }],
          "Assignments": { "file:kept": "{{boxId}}" },
          "OrganizationRules": [{
            "Id": "{{ruleId}}",
            "Title": "文档",
            "Priority": 30,
            "ItemKinds": [0],
            "NamePattern": "*",
            "Extensions": ["doc", "docx", "pdf", "rtf", "txt", "xls", "xlsx", "ppt", "pptx", "md"],
            "TargetBoxId": "{{boxId}}"
          }]
        }
        """);

        var loaded = await new JsonLayoutStore(_root).LoadAsync();

        Assert.Equal(5, loaded.OrganizationRules.Count);
        var documents = Assert.Single(loaded.OrganizationRules.Where(rule =>
            rule.BuiltInId == BuiltInOrganizationRules.DocumentsId));
        Assert.Equal(ruleId, documents.Id);
        Assert.Equal(boxId, documents.TargetBoxId);
    }

    [Fact]
    public async Task VersionSeventeenPreservesEditedAndDeletedBuiltIns()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), """
        {
          "SchemaVersion": 17,
          "Boxes": [],
          "Assignments": {},
          "OrganizationRules": [{
            "BuiltInId": "documents",
            "Title": "我的资料",
            "Priority": 90,
            "ItemKinds": [0],
            "NamePattern": "Work-*",
            "Extensions": ["work"]
          }]
        }
        """);

        var loaded = await new JsonLayoutStore(_root).LoadAsync();

        var rule = Assert.Single(loaded.OrganizationRules);
        Assert.Equal(BuiltInOrganizationRules.DocumentsId, rule.BuiltInId);
        Assert.Equal("我的资料", rule.Title);
        Assert.Equal(90, rule.Priority);
        Assert.Equal("Work-*", rule.NamePattern);
        Assert.Equal([".work"], rule.Extensions);
    }

    [Fact]
    public async Task VersionSeventeenMigratesOldDefaultIconSpacingToCompactGrid()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), """
        {
          "SchemaVersion": 17,
          "Settings": {
            "Appearance": {
              "IconHorizontalSpacing": 82,
              "IconVerticalSpacing": 88
            }
          },
          "Boxes": [],
          "Assignments": {},
          "OrganizationRules": []
        }
        """);

        var loaded = await new JsonLayoutStore(_root).LoadAsync();

        Assert.Equal(18, loaded.SchemaVersion);
        Assert.Equal(76, loaded.Settings.Appearance.IconHorizontalSpacing);
        Assert.Equal(80, loaded.Settings.Appearance.IconVerticalSpacing);
    }

    [Fact]
    public async Task VersionSeventeenPreservesCustomizedIconSpacing()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), """
        {
          "SchemaVersion": 17,
          "Settings": {
            "Appearance": {
              "IconHorizontalSpacing": 91,
              "IconVerticalSpacing": 103
            }
          },
          "Boxes": [],
          "Assignments": {},
          "OrganizationRules": []
        }
        """);

        var loaded = await new JsonLayoutStore(_root).LoadAsync();

        Assert.Equal(91, loaded.Settings.Appearance.IconHorizontalSpacing);
        Assert.Equal(103, loaded.Settings.Appearance.IconVerticalSpacing);
    }

    [Fact]
    public async Task CorruptPrimaryFallsBackToBackup()
    {
        var store = new JsonLayoutStore(_root);
        var state = JsonLayoutStore.CreateDefaultState();
        state.Boxes.Add(new DesktopBox { MonitorId = "primary" });
        await store.SaveAsync(state);
        state.Boxes[0].Title = "备份版本";
        await store.SaveAsync(state);
        await File.WriteAllTextAsync(store.StatePath, "not-json");

        var loaded = await store.LoadAsync();

        Assert.NotEmpty(loaded.Boxes);
    }

    [Fact]
    public async Task VersionOneStateDropsLegacyAutomaticAssignments()
    {
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "config.json");
        await File.WriteAllTextAsync(statePath, """
        {
          "SchemaVersion": 1,
          "Boxes": [{ "Id": "11111111-1111-1111-1111-111111111111", "Title": "未分类", "MonitorId": "primary", "Bounds": { "X": 10, "Y": 10, "Width": 300, "Height": 200 } }],
          "Assignments": { "file:old": "11111111-1111-1111-1111-111111111111" }
        }
        """);

        var loaded = await new JsonLayoutStore(_root).LoadAsync();

        Assert.Equal(18, loaded.SchemaVersion);
        Assert.Empty(loaded.Assignments);
        Assert.Single(loaded.Boxes);
        Assert.Equal("常用", loaded.Boxes[0].Title);
        Assert.True(loaded.MigratedFromLegacyIconTakeover);
    }

    [Fact]
    public async Task VersionTwoStateGetsBehaviorAndAppearanceDefaultsWithoutLosingAssignments()
    {
        Directory.CreateDirectory(_root);
        var boxId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), $$"""
        {
          "SchemaVersion": 2,
          "Settings": { "TakeOverDesktop": true },
          "Boxes": [{ "Id": "{{boxId}}", "Title": "常用", "MonitorId": "primary", "Bounds": { "X": 10, "Y": 10, "Width": 300, "Height": 200 } }],
          "Assignments": { "file:kept": "{{boxId}}" }
        }
        """);

        var loaded = await new JsonLayoutStore(_root).LoadAsync();

        Assert.Equal(18, loaded.SchemaVersion);
        Assert.Equal(boxId, loaded.Assignments["file:kept"]);
        Assert.Equal(8, loaded.Settings.Appearance.CornerRadius);
        Assert.True(loaded.Settings.DesktopBehavior.RefreshAfterRename);
    }

    [Fact]
    public async Task VersionThirteenDemoLayoutMigratesToEmptyDesktop()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), """
        {
          "SchemaVersion": 13,
          "Settings": { "DesktopBehavior": { "ShowDesktopContextMenu": false } },
          "Boxes": [
            { "Title": "常用", "MonitorId": "primary", "Bounds": { "X": 36, "Y": 52, "Width": 520, "Height": 420 } },
            { "Title": "工作", "MonitorId": "primary", "Bounds": { "X": 584, "Y": 52, "Width": 360, "Height": 250 } }
          ],
          "Assignments": {},
          "OrganizationRules": []
        }
        """);

        var loaded = await new JsonLayoutStore(_root).LoadAsync();

        Assert.Equal(18, loaded.SchemaVersion);
        Assert.Empty(loaded.Boxes);
        Assert.False(loaded.Settings.TakeOverDesktop);
    }

    [Fact]
    public async Task VersionFifteenTakeoverPreferenceIsPreserved()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), """
        {
          "SchemaVersion": 15,
          "Settings": { "TakeOverDesktop": true },
          "Boxes": [],
          "Assignments": {}
        }
        """);

        var loaded = await new JsonLayoutStore(_root).LoadAsync();

        Assert.Equal(18, loaded.SchemaVersion);
        Assert.True(loaded.Settings.TakeOverDesktop);
    }

    [Fact]
    public async Task VersionThirteenEmptyBuiltInRuleBoxesAreRemoved()
    {
        Directory.CreateDirectory(_root);
        var boxId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), $$"""
        {
          "SchemaVersion": 13,
          "Boxes": [{
            "Id": "{{boxId}}",
            "Title": "文档",
            "MonitorId": "primary",
            "Bounds": { "X": 80, "Y": 90, "Width": 360, "Height": 280 }
          }],
          "Assignments": {},
          "OrganizationRules": [{
            "Title": "文档",
            "TargetBoxId": "{{boxId}}",
            "ItemKinds": [0],
            "Extensions": ["pdf"]
          }]
        }
        """);

        var loaded = await new JsonLayoutStore(_root).LoadAsync();

        Assert.Empty(loaded.Boxes);
        Assert.Equal(5, loaded.OrganizationRules.Count);
        Assert.All(loaded.OrganizationRules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.BuiltInId)));
    }

    [Fact]
    public async Task MappedFolderSettingsPersistAndCannotOwnDesktopAssignments()
    {
        var store = new JsonLayoutStore(_root);
        var state = JsonLayoutStore.CreateDefaultState();
        var mappedBox = new DesktopBox
        {
            Title = "项目目录",
            MonitorId = "primary",
            MappedFolder = new MappedFolderSettings
            {
                Path = Path.Combine(_root, "project"),
                IsReadOnly = true
            }
        };
        state.Boxes.Add(mappedBox);
        state.Assignments["file:invalid-mapped-assignment"] = mappedBox.Id;

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        var restored = Assert.Single(loaded.Boxes.Where(box => box.Id == mappedBox.Id));
        Assert.True(restored.IsMappedFolder);
        Assert.True(restored.MappedFolder!.IsReadOnly);
        Assert.Equal(Path.Combine(_root, "project"), restored.MappedFolder.Path);
        Assert.DoesNotContain("file:invalid-mapped-assignment", loaded.Assignments);
        Assert.Equal(18, loaded.SchemaVersion);
    }

    [Fact]
    public async Task VersionElevenAppearanceMigratesToOpaqueSchemaTwelveDefaults()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), """
        {
          "SchemaVersion": 11,
          "Boxes": [{
            "Id": "11111111-1111-1111-1111-111111111111",
            "Title": "旧外观",
            "MonitorId": "primary",
            "Bounds": { "X": 10, "Y": 10, "Width": 300, "Height": 200 },
            "Appearance": { "Background": "#D92A2D32", "Accent": "#FF4EA1D3", "Opacity": 0.88, "IconSize": 42 }
          }],
          "Assignments": {}
        }
        """);

        var loaded = await new JsonLayoutStore(_root).LoadAsync();
        var appearance = loaded.Boxes[0].Appearance;

        Assert.Equal(18, loaded.SchemaVersion);
        Assert.Equal("#FF2A2D32", appearance.Background);
        Assert.Equal(1, appearance.Opacity);
        Assert.Equal(38, appearance.TitleBarHeight);
        Assert.Equal("Segoe UI", appearance.TitleFontFamily);
        Assert.Equal("Segoe UI", appearance.LabelFontFamily);
        Assert.Equal(8.5, appearance.LabelFontSize);
        Assert.True(appearance.ShowItemLabels);
    }

    [Theory]
    [InlineData(0.1, 0.35)]
    [InlineData(1.4, 1.0)]
    public async Task BoxOpacityIsClampedWhenStateIsSaved(double opacity, double expected)
    {
        var store = new JsonLayoutStore(_root);
        var state = JsonLayoutStore.CreateDefaultState("primary");
        state.Boxes.Add(new DesktopBox { MonitorId = "primary" });
        state.Boxes[0].Appearance.Opacity = opacity;

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.Equal(expected, loaded.Boxes[0].Appearance.Opacity);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
