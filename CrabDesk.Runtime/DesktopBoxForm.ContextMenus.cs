using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices.ComTypes;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;
using FormsIntegration = System.Windows.Forms.Integration;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm : Forms.Form
{

    private Forms.ContextMenuStrip BuildBoxMenu(DesktopBox box)
    {
        var menu = CreateContextMenu();
        if (box.IsMappedFolder)
        {
            menu.Items.Add("打开映射文件夹", null, (_, _) =>
                TryAction(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(box.MappedFolder!.Path)
                {
                    UseShellExecute = true
                })));
            menu.Items.Add(new Forms.ToolStripSeparator());
        }
        var paste = new Forms.ToolStripMenuItem("粘贴")
        {
            Enabled = _runtime.CanPasteIntoBox(box)
        };
        paste.Click += async (_, _) => await PasteIntoBoxAsync(box);
        menu.Items.Add(paste);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("重命名", null, (_, _) =>
        {
            BeginInvoke((Action)(() => BeginTitleEdit(box)));
        });
        var displayModeMenu = new Forms.ToolStripMenuItem("显示模式");
        AddMenuChoice(
            displayModeMenu,
            "固定展开",
            !box.ExpandOnHover,
            () => SetBoxDisplayMode(box, expandOnHover: false));
        AddMenuChoice(
            displayModeMenu,
            "悬停自动展开",
            box.ExpandOnHover,
            () => SetBoxDisplayMode(box, expandOnHover: true));
        menu.Items.Add(displayModeMenu);
        if (!box.IsMappedFolder)
        {
            AddManualTabMenu(menu, box);
        }
        var accentMenu = new Forms.ToolStripMenuItem("颜色条颜色");
        var stackMenu = new Forms.ToolStripMenuItem("层级");
        stackMenu.DropDownItems.Add("置于顶层", null, (_, _) =>
            _runtime.MoveBoxInStack(box.Id, BoxStackMove.ToFront));
        stackMenu.DropDownItems.Add("上移一层", null, (_, _) =>
            _runtime.MoveBoxInStack(box.Id, BoxStackMove.Forward));
        stackMenu.DropDownItems.Add("下移一层", null, (_, _) =>
            _runtime.MoveBoxInStack(box.Id, BoxStackMove.Backward));
        stackMenu.DropDownItems.Add("置于底层", null, (_, _) =>
            _runtime.MoveBoxInStack(box.Id, BoxStackMove.ToBack));
        menu.Items.Add(stackMenu);

        foreach (var (name, hex) in AccentPalette)
        {
            AddMenuChoice(
                accentMenu,
                name,
                string.Equals(box.Appearance.Accent, hex, StringComparison.OrdinalIgnoreCase),
                () => _runtime.SetBoxAccent(box.Id, hex));
        }
        accentMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        accentMenu.DropDownItems.Add("自定义颜色…", null, (_, _) => ShowAccentColorDialog(box));
        menu.Items.Add(accentMenu);

        var viewMenu = new Forms.ToolStripMenuItem("视图");
        AddMenuChoice(viewMenu, "图标", box.ViewMode == BoxViewMode.Grid,
            () => _runtime.SetBoxViewMode(box.Id, BoxViewMode.Grid));
        AddMenuChoice(viewMenu, "列表", box.ViewMode == BoxViewMode.List,
            () => _runtime.SetBoxViewMode(box.Id, BoxViewMode.List));
        menu.Items.Add(viewMenu);

        var sortMenu = new Forms.ToolStripMenuItem("排序方式");
        AddMenuChoice(sortMenu, "手动", box.SortMode == BoxSortMode.Manual,
            () => _runtime.SetBoxSortMode(box.Id, BoxSortMode.Manual));
        AddMenuChoice(sortMenu, "名称", box.SortMode == BoxSortMode.Name,
            () => _runtime.SetBoxSortMode(box.Id, BoxSortMode.Name));
        AddMenuChoice(sortMenu, "类型", box.SortMode == BoxSortMode.Type,
            () => _runtime.SetBoxSortMode(box.Id, BoxSortMode.Type));
        AddMenuChoice(sortMenu, "修改时间", box.SortMode == BoxSortMode.Modified,
            () => _runtime.SetBoxSortMode(box.Id, BoxSortMode.Modified));
        menu.Items.Add(sortMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => _runtime.RequestShowSettings("appearance"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("删除盒子", null, async (_, _) =>
        {
            try
            {
                var detail = box.IsMappedFolder
                    ? "不会删除映射文件夹或其中的文件。"
                    : "盒子中的文件仍保留在桌面。";
                if (!_runtime.State.Settings.ConfirmDeleteBox ||
                    await ConfirmDesktopAsync(
                        $"删除“{box.Title}”？",
                        detail,
                        "删除盒子"))
                {
                    _runtime.DeleteBox(box);
                }
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error($"Delete box failed: {exception}", exception);
            }
        });
        return menu;
    }

    private void AddManualTabMenu(Forms.ContextMenuStrip menu, DesktopBox box)
    {
        var tabMenu = new Forms.ToolStripMenuItem("子标签");
        tabMenu.DropDownItems.Add("新建子标签…", null, (_, _) =>
            BeginInvoke((Action)(() => CreateManualTab(box))));

        var activeTabId = _activeManualTabIds.GetValueOrDefault(box.Id);
        var activeTab = activeTabId is { } id
            ? box.ManualTabs.FirstOrDefault(tab => tab.Id == id)
            : null;
        if (activeTab is not null)
        {
            tabMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
            tabMenu.DropDownItems.Add("重命名当前标签…", null, (_, _) =>
                BeginInvoke((Action)(() => RenameManualTab(box, activeTab))));
            tabMenu.DropDownItems.Add("删除当前标签", null, (_, _) =>
                BeginInvoke((Action)(async () => await DeleteManualTab(box, activeTab))));
        }

        var selectedKeys = GetSelectedItemKeys(box.Id);
        if (box.ManualTabs.Count > 0 && selectedKeys.Length > 0)
        {
            var moveMenu = new Forms.ToolStripMenuItem("将选中图标移到");
            moveMenu.DropDownItems.Add("全部（移出子标签）", null, (_, _) =>
                MoveSelectedItemsToManualTab(box, selectedKeys, null));
            foreach (var tab in box.ManualTabs)
            {
                var targetTab = tab;
                moveMenu.DropDownItems.Add(targetTab.Title, null, (_, _) =>
                    MoveSelectedItemsToManualTab(box, selectedKeys, targetTab.Id));
            }
            tabMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
            tabMenu.DropDownItems.Add(moveMenu);
        }

        menu.Items.Add(tabMenu);
    }

    private string[] GetSelectedItemKeys(Guid boxId) => GetCachedItemsForBox(boxId)
        .Select(item => item.Key.ToString())
        .Where(_selection.Contains)
        .ToArray();

    private void CreateManualTab(DesktopBox box)
    {
        var title = PromptForManualTabTitle("新建子标签", "标签名称", "新标签");
        if (title is null)
        {
            return;
        }

        var tab = _runtime.CreateManualTab(box.Id, title);
        _activeManualTabIds[box.Id] = tab.Id;
        ClearBoxItemSelection(box.Id);
        Invalidate();
    }

    private void RenameManualTab(DesktopBox box, DesktopBoxTab tab)
    {
        var title = PromptForManualTabTitle("重命名子标签", "标签名称", tab.Title);
        if (title is not null)
        {
            _runtime.RenameManualTab(box.Id, tab.Id, title);
        }
    }

    private async Task DeleteManualTab(DesktopBox box, DesktopBoxTab tab)
    {
        if (!await ConfirmDesktopAsync(
                $"删除“{tab.Title}”标签？",
                "该标签中的图标会保留在盒子里，并回到“全部”。",
                "删除标签"))
        {
            return;
        }

        if (_runtime.DeleteManualTab(box.Id, tab.Id))
        {
            _activeManualTabIds[box.Id] = null;
            ClearBoxItemSelection(box.Id);
            Invalidate();
        }
    }
    private async Task<bool> ConfirmDesktopAsync(string title, string message, string primaryText)
    {
        if (_confirmationInProgress)
        {
            return false;
        }
        var handler = _runtime.DesktopConfirmationHandler;
        if (handler is null)
        {
            return DesktopConfirmationDialog.Show(this, _runtime.IsDarkTheme, title, message, primaryText);
        }
        _confirmationInProgress = true;
        try
        {
            return await handler(new DesktopConfirmationRequest(Handle, title, message, primaryText));
        }
        finally
        {
            _confirmationInProgress = false;
        }
    }

    private async Task PasteIntoBoxAsync(DesktopBox box)
    {
        if (!_runtime.CanPasteIntoBox(box))
        {
            return;
        }

        try
        {
            var result = await _runtime.PasteIntoBoxAsync(box.Id);
            ShowImportFailures(result.ImportResult);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error($"Failed to paste files into box '{box.Title}'.", exception);
            Forms.MessageBox.Show(
                this,
                exception.Message,
                "粘贴失败",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
    }

    private void ShowImportFailures(FileImportBatchResult result)
    {
        if (!result.HasFailures)
        {
            return;
        }

        var details = string.Join(
            Environment.NewLine,
            result.FailedItems.Take(3).Select(item =>
                $"- {Path.GetFileName(item.SourcePath)}: {item.ErrorMessage}"));
        if (result.FailedCount > 3)
        {
            details += Environment.NewLine + $"另有 {result.FailedCount - 3} 项未导入。";
        }

        Forms.MessageBox.Show(
            this,
            $"已导入 {result.SucceededCount} 项，{result.FailedCount} 项未导入。{Environment.NewLine}{Environment.NewLine}{details}",
            "导入未完成",
            Forms.MessageBoxButtons.OK,
            Forms.MessageBoxIcon.Warning);
    }

    private void MoveSelectedItemsToManualTab(DesktopBox box, IEnumerable<string> itemKeys, Guid? tabId)
    {
        if (_runtime.MoveItemsToManualTab(box.Id, itemKeys, tabId) > 0)
        {
            ClearBoxItemSelection(box.Id);
            Invalidate();
        }
    }

    private string? PromptForManualTabTitle(string title, string label, string initialValue)
    {
        var isDark = _runtime.IsDarkTheme;
        using var dialog = new Forms.Form
        {
            Text = title,
            AccessibleName = title,
            AutoScaleMode = Forms.AutoScaleMode.Dpi,
            BackColor = isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250),
            ClientSize = new Size(360, 160),
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = Forms.FormStartPosition.CenterParent,
            Font = CreateFont("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point)
        };
        var foreground = isDark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(31, 31, 31);
        var input = new Forms.TextBox
        {
            AccessibleName = label,
            Font = CreateFont("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            Location = new Point(20, 54),
            Size = new Size(320, 28),
            Text = initialValue
        };
        var labelControl = new Forms.Label
        {
            AutoSize = true,
            ForeColor = foreground,
            Location = new Point(20, 24),
            Text = label
        };
        var cancel = new Forms.Button
        {
            DialogResult = Forms.DialogResult.Cancel,
            Location = new Point(174, 108),
            Size = new Size(78, 30),
            Text = "取消"
        };
        var confirm = new Forms.Button
        {
            DialogResult = Forms.DialogResult.OK,
            Location = new Point(262, 108),
            Size = new Size(78, 30),
            Text = "确定"
        };
        dialog.Controls.AddRange([labelControl, input, cancel, confirm]);
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) =>
        {
            input.SelectAll();
            input.Focus();
        };
        return dialog.ShowDialog(this) == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text)
            ? input.Text.Trim()
            : null;
    }

    private void ShowAccentColorDialog(DesktopBox box)
    {
        using var dialog = new Forms.ColorDialog
        {
            Color = ParseOpaqueColor(box.Appearance.Accent),
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = true
        };
        if (dialog.ShowDialog(this) != Forms.DialogResult.OK)
        {
            return;
        }

        var color = dialog.Color;
        _runtime.SetBoxAccent(box.Id, $"#FF{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private void ShowItemContextMenu(DesktopBox box, DesktopItemRef item, Point location)
    {
        var selectedItems = GetCachedItemsForBox(box.Id)
            .Where(candidate => _selection.Contains(candidate.Key.ToString()))
            .ToArray();
        if (selectedItems.Length == 0)
        {
            selectedItems = [item];
        }
        DiagnosticLog.Info(
            $"Box context menu box={box.Id:N} selection={_selection.Count} items={selectedItems.Length} " +
            $"clicked={item.DisplayName}");
        if (item.FileSystemPath is { } clickedPath)
        {
            var clickedParent = Path.GetDirectoryName(Path.GetFullPath(clickedPath));
            selectedItems = selectedItems
                .Where(candidate => candidate.FileSystemPath is { } candidatePath &&
                    string.Equals(
                        Path.GetDirectoryName(Path.GetFullPath(candidatePath)),
                        clickedParent,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            selectedItems = [item];
        }
        if (selectedItems.Length == 0)
        {
            selectedItems = [item];
        }

        var session = ShellContextMenuSession.TryCreate(
                selectedItems.Select(candidate => candidate.ParsingName),
                Handle)
            ?? ShellContextMenuSession.TryCreate([item.ParsingName], Handle);
        if (session is null)
        {
            return;
        }
        var canRename = box.MappedFolder?.IsReadOnly != true &&
            selectedItems.Length == 1 && selectedItems[0].FileSystemPath is not null;
        var command = ShellContextMenuCommand.None;
        _shellContextMenu = session;
        try
        {
            var screenPoint = PointToScreen(location);
            command = session.Show(
                Handle,
                screenPoint.X,
                screenPoint.Y,
                canRename,
                box.MappedFolder?.IsReadOnly == true
                    ? ShellContextMenuRestrictions.BlockFileMutations
                    : ShellContextMenuRestrictions.None);
        }
        finally
        {
            _shellContextMenu = null;
            session.Dispose();
        }

        if (command == ShellContextMenuCommand.Rename && canRename)
        {
            _ = RenameItemAsync(box, selectedItems[0]);
        }
    }

    private async Task RenameItemAsync(DesktopBox box, DesktopItemRef item)
    {
        if (box.MappedFolder?.IsReadOnly == true)
        {
            return;
        }

        var newName = await ShowInlineRenameAsync(box, item);
        if (newName is null ||
            string.Equals(newName, item.DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _runtime.RenameItemAsync(item, newName, box.Id);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error($"Failed to rename box item '{item.DisplayName}'.", exception);
            Forms.MessageBox.Show(
                this,
                exception.Message,
                "重命名失败",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
    }

    private async Task<string?> ShowInlineRenameAsync(DesktopBox box, DesktopItemRef item)
    {
        var geometry = _items.LastOrDefault(candidate =>
            candidate.Box.Id == box.Id &&
            string.Equals(
                candidate.Item.Key.ToString(),
                item.Key.ToString(),
                StringComparison.OrdinalIgnoreCase));
        if (geometry is null)
        {
            return null;
        }

        _renameEditor ??= new DesktopRenameEditor();
        var labelBounds = GetItemLabelEditBounds(geometry);
        var scale = (float)Math.Max(_scale, 0.01d);
        var screenLocation = PointToScreen(new Point(
            (int)Math.Round(labelBounds.X * scale),
            (int)Math.Round(labelBounds.Y * scale)));
        var selectStem = item.Kind == DesktopItemKind.File ||
            item.Kind == DesktopItemKind.Shortcut;
        using var labelFont = CreateFont(
            geometry.Box.Appearance.LabelFontFamily,
            (float)geometry.Box.Appearance.LabelFontSize,
            FontStyle.Regular,
            GraphicsUnit.Point);
        _renamingBoxId = box.Id;
        _renamingItemKey = item.Key.ToString();
        RequestVisualLayerRender();
        try
        {
            return await _renameEditor.ShowAsync(
                screenLocation,
                new Size(
                    (int)Math.Round(labelBounds.Width * scale),
                    (int)Math.Round(labelBounds.Height * scale)),
                item.DisplayName,
                selectStem,
                _runtime.IsDarkTheme,
                labelFont,
                wordWrap: geometry.Box.ViewMode != BoxViewMode.List);
        }
        finally
        {
            _renamingBoxId = null;
            _renamingItemKey = null;
            RequestVisualLayerRender();
        }
    }

    private RectangleF GetItemLabelEditBounds(ItemGeometry item)
    {
        var iconBounds = GetItemIconBounds(item);
        using var measureBitmap = DesktopLayerBitmapFactory.Create(1, 1);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        using var labelFont = CreateFont(
            item.Box.Appearance.LabelFontFamily,
            (float)item.Box.Appearance.LabelFontSize,
            FontStyle.Regular,
            GraphicsUnit.Point);
        var lineHeight = Math.Max(1, labelFont.GetHeight(measureGraphics));

        if (item.Box.ViewMode == BoxViewMode.List)
        {
            // The list row keeps the full text column as the edit area, like
            // Explorer's list-view rename box.
            var listRelativeWorkArea = MonitorCoordinateConverter.GetMonitorRelativeWorkArea(_monitor);
            var listWorkArea = new RectangleF(
                (float)listRelativeWorkArea.X,
                (float)listRelativeWorkArea.Y,
                (float)listRelativeWorkArea.Width,
                (float)listRelativeWorkArea.Height);
            var textColumnWidth = Math.Max(40, item.Bounds.Right - iconBounds.Right - 18);
            var listWidth = DesktopRenameEditor.CalculateEditorWidth(
                textColumnWidth,
                Math.Max(48, listWorkArea.Width - 8));
            var listCenterX = iconBounds.Right + 10 + textColumnWidth / 2;
            var listLeft = Math.Max(
                listWorkArea.Left + 2,
                Math.Min(listCenterX - listWidth / 2, listWorkArea.Right - listWidth - 2));
            var listHeight = Math.Max(lineHeight + 2, item.Bounds.Height - 2);
            var listTop = Math.Max(
                listWorkArea.Top + 1,
                item.Bounds.Y + (item.Bounds.Height - listHeight) / 2);
            if (listTop + listHeight > listWorkArea.Bottom - 1)
            {
                listTop = Math.Max(listWorkArea.Top + 1, listWorkArea.Bottom - listHeight - 1);
            }
            return new RectangleF(listLeft, listTop, listWidth, listHeight);
        }

        var textTop = iconBounds.Bottom + 3;
        var textWidth = Math.Max(0, item.Bounds.Width - 4);
        var layout = new RectangleF(
            item.Bounds.X + 2,
            textTop,
            textWidth,
            Math.Max(
                0,
                Math.Min(
                    item.Bounds.Bottom - textTop - 3,
                    labelFont.GetHeight(measureGraphics) * CompactGridLabelLineCount + 2)));
        var relativeWorkArea = MonitorCoordinateConverter.GetMonitorRelativeWorkArea(_monitor);
        var workArea = new RectangleF(
            (float)relativeWorkArea.X,
            (float)relativeWorkArea.Y,
            (float)relativeWorkArea.Width,
            (float)relativeWorkArea.Height);
        var width = DesktopRenameEditor.CalculateEditorWidth(
            Math.Max(1, layout.Width),
            Math.Max(48, workArea.Width - 8));
        var centerX = layout.X + layout.Width / 2;
        var left = Math.Max(
            workArea.Left + 2,
            Math.Min(centerX - width / 2, workArea.Right - width - 2));
        // Keep the grid label width, then grow only downward for however many
        // lines the complete name needs at that same wrap width. List mode is
        // handled above with its native single-row label region.
        var wrappedTextHeight = MeasureFullGridLabelHeight(
            measureGraphics,
            item.Item.DisplayName,
            labelFont,
            width);
        var height = DesktopRenameEditor.CalculateEditorHeight(
            wrappedTextHeight,
            lineHeight,
            Math.Max(1, workArea.Height - 2));
        var top = Math.Max(workArea.Top + 1, textTop);
        if (top + height > workArea.Bottom - 1)
        {
            top = Math.Max(workArea.Top + 1, workArea.Bottom - height - 1);
        }
        return new RectangleF(left, top, width, height);
    }

    private static RectangleF MeasureLabelFootprint(
        Graphics graphics,
        string displayName,
        RectangleF textBounds,
        Font font)
    {
        if (textBounds.Width <= 0 || textBounds.Height <= 0 || string.IsNullOrWhiteSpace(displayName))
        {
            return RectangleF.Empty;
        }

        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit
        };
        var measured = graphics.MeasureString(displayName, font, textBounds.Size, format);
        var width = Math.Min(textBounds.Width, Math.Max(font.Size, measured.Width));
        var height = Math.Min(textBounds.Height, Math.Max(0, measured.Height));
        return new RectangleF(
            textBounds.X + (textBounds.Width - width) / 2,
            textBounds.Y,
            width,
            height);
    }

    private Forms.ContextMenuStrip CreateContextMenu()
    {
        var menu = new FluentContextMenuStrip();
        menu.Opening += (_, _) => _runtime.ApplyContextMenuTheme(menu);
        menu.Opened += (_, _) => _runtime.ApplyContextMenuTheme(menu);
        // ContextMenuStrip is still referenced by ToolStripManager while the
        // Closed event is running. Disposing it synchronously here leaves a
        // disposed active drop-down behind and crashes on the next mouse press.
        menu.Closed += (_, _) =>
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }
            BeginInvoke((Action)(() => menu.Dispose()));
        };
        _runtime.ApplyContextMenuTheme(menu);
        return menu;
    }

    private static void AddMenuChoice(
        Forms.ToolStripMenuItem parent,
        string text,
        bool isChecked,
        Action action)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            Checked = isChecked,
            CheckOnClick = false
        };
        item.Click += (_, _) => action();
        parent.DropDownItems.Add(item);
    }

}

