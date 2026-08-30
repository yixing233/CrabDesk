using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CrabDesk.Bootstrapper;

internal static class InstallerRenderer
{
    public const int BaseWidth = 640;
    public const int BaseHeight = 450;
    public const int BaseTitleBarHeight = 38;

    public static int GetScaledWidth(float scale) => (int)Math.Round(BaseWidth * scale);
    public static int GetScaledHeight(float scale) => (int)Math.Round(BaseHeight * scale);
    public static int GetScaledTitleBarHeight(float scale) => (int)Math.Round(BaseTitleBarHeight * scale);

    public static Rectangle GetCloseButtonBounds(float scale)
    {
        var w = GetScaledWidth(scale);
        var tb = GetScaledTitleBarHeight(scale);
        var bw = (int)(44 * scale);
        return new Rectangle(w - bw, 0, bw, tb);
    }

    public static Rectangle GetMinButtonBounds(float scale)
    {
        var w = GetScaledWidth(scale);
        var tb = GetScaledTitleBarHeight(scale);
        var bw = (int)(44 * scale);
        return new Rectangle(w - bw * 2, 0, bw, tb);
    }

    public static Rectangle GetPathBoxBounds(float scale)
    {
        var cardLeft = (int)(36 * scale);
        var cardWidth = GetScaledWidth(scale) - (int)(72 * scale);
        var pathY = (int)(250 * scale);
        var labelWidth = (int)(80 * scale);
        var btnWidth = (int)(80 * scale);
        var spacing = (int)(10 * scale);

        var boxX = cardLeft + labelWidth;
        var boxW = cardWidth - labelWidth - btnWidth - spacing;
        var boxH = (int)(32 * scale);
        return new Rectangle(boxX, pathY, boxW, boxH);
    }

    public static Rectangle GetBrowseButtonBounds(float scale)
    {
        var box = GetPathBoxBounds(scale);
        var btnW = (int)(80 * scale);
        var spacing = (int)(10 * scale);
        return new Rectangle(box.Right + spacing, box.Y, btnW, box.Height);
    }

    public static Rectangle GetDesktopShortcutCheckBounds(float scale)
    {
        var x = (int)(36 * scale);
        var y = (int)(320 * scale);
        var w = (int)(180 * scale);
        var h = (int)(24 * scale);
        return new Rectangle(x, y, w, h);
    }

    public static Rectangle GetContextMenuCheckBounds(float scale)
    {
        var x = (int)(230 * scale);
        var y = (int)(320 * scale);
        var w = (int)(180 * scale);
        var h = (int)(24 * scale);
        return new Rectangle(x, y, w, h);
    }

    public static Rectangle GetInstallButtonBounds(float scale)
    {
        var winW = GetScaledWidth(scale);
        var winH = GetScaledHeight(scale);
        var btnW = (int)(92 * scale);
        var btnH = (int)(34 * scale);
        var x = winW - (int)(36 * scale) - btnW;
        var y = winH - (int)(50 * scale);
        return new Rectangle(x, y, btnW, btnH);
    }

    public static Rectangle GetCancelButtonBounds(float scale)
    {
        var install = GetInstallButtonBounds(scale);
        var spacing = (int)(12 * scale);
        return new Rectangle(install.Left - spacing - install.Width, install.Y, install.Width, install.Height);
    }

    public static Rectangle GetLaunchCheckBounds(float scale)
    {
        var winW = GetScaledWidth(scale);
        var w = (int)(180 * scale);
        var h = (int)(24 * scale);
        return new Rectangle((winW - w) / 2, (int)(240 * scale), w, h);
    }

    public static Rectangle GetFinishButtonBounds(float scale)
    {
        var winW = GetScaledWidth(scale);
        var winH = GetScaledHeight(scale);
        var w = (int)(110 * scale);
        var h = (int)(36 * scale);
        return new Rectangle((winW - w) / 2, winH - (int)(62 * scale), w, h);
    }

    public static Rectangle GetRetryButtonBounds(float scale)
    {
        var winW = GetScaledWidth(scale);
        var winH = GetScaledHeight(scale);
        var w = (int)(88 * scale);
        var h = (int)(34 * scale);
        return new Rectangle(winW / 2 - w - (int)(8 * scale), winH - (int)(60 * scale), w, h);
    }

    public static Rectangle GetErrorCloseButtonBounds(float scale)
    {
        var winW = GetScaledWidth(scale);
        var winH = GetScaledHeight(scale);
        var w = (int)(88 * scale);
        var h = (int)(34 * scale);
        return new Rectangle(winW / 2 + (int)(8 * scale), winH - (int)(60 * scale), w, h);
    }

    public static void Render(Graphics g, InstallerTheme theme, InstallerState state)
    {
        var scale = theme.DpiScale;
        var winW = GetScaledWidth(scale);
        var winH = GetScaledHeight(scale);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // Background
        using (var bgBrush = new SolidBrush(theme.Background))
        {
            g.FillRectangle(bgBrush, 0, 0, winW, winH);
        }

        // Title Bar
        RenderTitleBar(g, theme, state);

        // Page Contents
        switch (state.Page)
        {
            case InstallerPage.Welcome:
                RenderWelcomePage(g, theme, state);
                break;
            case InstallerPage.Installing:
                RenderInstallingPage(g, theme, state);
                break;
            case InstallerPage.Completed:
                RenderCompletedPage(g, theme, state);
                break;
            case InstallerPage.Error:
                RenderErrorPage(g, theme, state);
                break;
        }

        // Window Border
        using (var borderPen = new Pen(theme.BorderColor, 1))
        {
            g.DrawRectangle(borderPen, 0, 0, winW - 1, winH - 1);
        }
    }

    private static void RenderTitleBar(Graphics g, InstallerTheme theme, InstallerState state)
    {
        var scale = theme.DpiScale;
        var tbH = GetScaledTitleBarHeight(scale);
        var logoSize = (int)(18 * scale);
        var logoY = (tbH - logoSize) / 2;

        LucideInstallerIcons.DrawAppLogo(g, new Rectangle((int)(14 * scale), logoY, logoSize, logoSize));

        using (var brush = new SolidBrush(theme.TextPrimary))
        {
            var textY = (tbH - (int)(14 * scale)) / 2;
            g.DrawString("CrabDesk 安装向导", theme.SmallFont, brush, (int)(38 * scale), textY);
        }

        // Minimize Button
        var minBounds = GetMinButtonBounds(scale);
        if (state.HoveredControlId == "min_btn")
        {
            using var hoverBrush = new SolidBrush(theme.ButtonSecondaryHover);
            g.FillRectangle(hoverBrush, minBounds);
        }
        using (var pen = new Pen(theme.TextSecondary, 1.2f))
        {
            var my = minBounds.Y + minBounds.Height / 2;
            g.DrawLine(pen, minBounds.X + (int)(16 * scale), my, minBounds.X + (int)(28 * scale), my);
        }

        // Close Button
        var closeBounds = GetCloseButtonBounds(scale);
        var isCloseHovered = state.HoveredControlId == "close_btn";
        if (isCloseHovered)
        {
            using var closeHoverBrush = new SolidBrush(Color.FromArgb(232, 17, 35));
            g.FillRectangle(closeHoverBrush, closeBounds);
        }
        using (var closePen = new Pen(isCloseHovered ? Color.White : theme.TextSecondary, 1.2f))
        {
            var cx = closeBounds.X + (closeBounds.Width - (int)(12 * scale)) / 2;
            var cy = closeBounds.Y + (closeBounds.Height - (int)(12 * scale)) / 2;
            var cs = (int)(11 * scale);
            g.DrawLine(closePen, cx, cy, cx + cs, cy + cs);
            g.DrawLine(closePen, cx + cs, cy, cx, cy + cs);
        }
    }

    private static void RenderWelcomePage(Graphics g, InstallerTheme theme, InstallerState state)
    {
        var scale = theme.DpiScale;
        var winW = GetScaledWidth(scale);
        var winH = GetScaledHeight(scale);

        // Header
        var headerX = (int)(36 * scale);
        var headerY = (int)(46 * scale);
        var logoSize = (int)(44 * scale);

        LucideInstallerIcons.DrawAppLogo(g, new Rectangle(headerX, headerY, logoSize, logoSize));

        var textLeft = headerX + logoSize + (int)(12 * scale);
        var title = "CrabDesk 桌面整理器";
        var titleSize = g.MeasureString(title, theme.TitleFont);

        using (var titleBrush = new SolidBrush(theme.TextPrimary))
        {
            g.DrawString(title, theme.TitleFont, titleBrush, textLeft, headerY - (int)(2 * scale));
        }

        // Version Badge: placed strictly AFTER title width to avoid ANY overlap
        var versionText = (state.IsUpgrade && !string.IsNullOrWhiteSpace(state.ExistingVersion) && state.ExistingVersion != state.Version)
            ? $"v{state.ExistingVersion} → v{state.Version}"
            : $"v{state.Version}";
        var versionSize = g.MeasureString(versionText, theme.BadgeFont);
        var badgeW = (int)(versionSize.Width + 14 * scale);
        var badgeH = (int)(20 * scale);
        var badgeX = (int)(textLeft + titleSize.Width + 8 * scale);
        var badgeY = headerY + (int)((titleSize.Height - badgeH) / 2);

        DrawBadge(g, theme, versionText, new Rectangle(badgeX, badgeY, badgeW, badgeH), theme.Accent, Color.FromArgb(35, theme.Accent));

        var subtitleText = (state.IsUpgrade && !string.IsNullOrWhiteSpace(state.ExistingVersion))
            ? $"检测到已安装版本 v{state.ExistingVersion}，将为您无缝覆盖升级至最新版"
            : "现代化桌面分区与智能文件归类工具";

        using (var subBrush = new SolidBrush(theme.TextSecondary))
        {
            g.DrawString(subtitleText, theme.NormalFont, subBrush, textLeft, headerY + titleSize.Height + (int)(2 * scale));
        }

        // Section 1: Dependencies Card
        var cardLeft = (int)(36 * scale);
        var cardTop = (int)(106 * scale);
        var cardWidth = winW - (int)(72 * scale);
        var cardHeight = (int)(132 * scale);
        var cardBounds = new Rectangle(cardLeft, cardTop, cardWidth, cardHeight);
        DrawCard(g, theme, cardBounds);

        // Header: Lucide ShieldCheck Icon + Title
        var iconY = cardTop + (int)(10 * scale);
        LucideInstallerIcons.DrawIcon(g, LucideIcon.ShieldCheck, new RectangleF(cardLeft + (int)(14 * scale), iconY + (int)(2 * scale), (int)(16 * scale), (int)(16 * scale)), theme.Accent, 15 * scale);

        using (var sectionTitleBrush = new SolidBrush(theme.TextPrimary))
        {
            g.DrawString("运行环境智能检测", theme.HeaderFont, sectionTitleBrush, cardLeft + (int)(36 * scale), iconY);
        }

        var depY = cardTop + (int)(38 * scale);
        var rowH = (int)(28 * scale);
        foreach (var dep in state.Dependencies)
        {
            var iconBounds = new RectangleF(cardLeft + (int)(16 * scale), depY + (int)(3 * scale), (int)(14 * scale), (int)(14 * scale));

            switch (dep.Status)
            {
                case DependencyCheckStatus.Checking:
                    LucideInstallerIcons.DrawIconRotated(g, LucideIcon.RefreshCw, iconBounds, theme.Accent, 13 * scale, state.CheckingAnimationAngle);
                    break;
                case DependencyCheckStatus.Installed:
                    LucideInstallerIcons.DrawIcon(g, LucideIcon.Check, iconBounds, theme.SuccessColor, 13 * scale);
                    break;
                case DependencyCheckStatus.Missing:
                    LucideInstallerIcons.DrawIcon(g, LucideIcon.Download, iconBounds, theme.WarningColor, 13 * scale);
                    break;
                case DependencyCheckStatus.Pending:
                default:
                    LucideInstallerIcons.DrawIcon(g, LucideIcon.Clock, iconBounds, theme.TextMuted, 13 * scale);
                    break;
            }

            var textBrushColor = dep.Status switch
            {
                DependencyCheckStatus.Checking => theme.TextPrimary,
                DependencyCheckStatus.Pending => theme.TextMuted,
                _ => theme.TextSecondary
            };

            using (var depNameBrush = new SolidBrush(textBrushColor))
            {
                g.DrawString(dep.Dependency.DisplayName, theme.SmallFont, depNameBrush, cardLeft + (int)(36 * scale), depY + (int)(2 * scale));
            }

            string badgeText;
            Color badgeColor;
            switch (dep.Status)
            {
                case DependencyCheckStatus.Checking:
                    badgeText = "检测中...";
                    badgeColor = theme.Accent;
                    break;
                case DependencyCheckStatus.Installed:
                    badgeText = "已就绪 ✓";
                    badgeColor = theme.SuccessColor;
                    break;
                case DependencyCheckStatus.Missing:
                    badgeText = "需自动下载";
                    badgeColor = theme.WarningColor;
                    break;
                case DependencyCheckStatus.Pending:
                default:
                    badgeText = "等待检测";
                    badgeColor = theme.TextMuted;
                    break;
            }

            var itemBadgeSize = g.MeasureString(badgeText, theme.BadgeFont);
            var itemBadgeW = (int)Math.Max(68 * scale, itemBadgeSize.Width + 14 * scale);
            var itemBadgeH = (int)(20 * scale);
            var itemBadgeX = cardLeft + cardWidth - (int)(14 * scale) - itemBadgeW;

            DrawBadge(g, theme, badgeText, new Rectangle(itemBadgeX, depY, itemBadgeW, itemBadgeH), badgeColor, Color.FromArgb(28, badgeColor));
            depY += rowH;
        }

        // Section 2: Path Row
        var pathBoxBounds = GetPathBoxBounds(scale);
        var pathIconY = pathBoxBounds.Y + (pathBoxBounds.Height - (int)(16 * scale)) / 2;
        LucideInstallerIcons.DrawIcon(g, LucideIcon.FolderOpen, new RectangleF(cardLeft, pathIconY, (int)(16 * scale), (int)(16 * scale)), theme.TextSecondary, 15 * scale);

        using (var pathLabelBrush = new SolidBrush(theme.TextSecondary))
        {
            var labelY = pathBoxBounds.Y + (pathBoxBounds.Height - (int)(16 * scale)) / 2;
            g.DrawString("安装路径：", theme.NormalFont, pathLabelBrush, cardLeft + (int)(22 * scale), labelY);
        }

        using (var pathBoxBg = new SolidBrush(theme.CardBackground))
        using (var pathBoxBorder = new Pen(theme.BorderColor, 1))
        {
            FillRoundedRectangle(g, pathBoxBg, pathBoxBounds, (int)(4 * scale));
            DrawRoundedRectangle(g, pathBoxBorder, pathBoxBounds, (int)(4 * scale));
        }

        var browseBounds = GetBrowseButtonBounds(scale);
        DrawButton(g, theme, "浏览...", browseBounds, false, state.HoveredControlId == "browse_btn", state.PressedControlId == "browse_btn", true);

        // Section 3: Space Information Row
        var spaceY = pathBoxBounds.Bottom + (int)(8 * scale);
        LucideInstallerIcons.DrawIcon(g, LucideIcon.HardDrive, new RectangleF(cardLeft, spaceY + (int)(2 * scale), (int)(14 * scale), (int)(14 * scale)), theme.TextMuted, 13 * scale);

        var (freeText, isSufficient, driveName) = state.GetAvailableSpaceInfo();
        var requiredText = $"所需空间：约 {state.GetRequiredSpaceText()}";
        var driveSuffix = string.IsNullOrEmpty(driveName) ? "" : $" ({driveName})";
        var availableText = $"可用空间：{freeText}{driveSuffix}";

        using (var reqBrush = new SolidBrush(theme.TextMuted))
        {
            g.DrawString(requiredText, theme.SmallFont, reqBrush, cardLeft + (int)(22 * scale), spaceY);
        }

        var reqSize = g.MeasureString(requiredText, theme.SmallFont);
        var availX = cardLeft + (int)(22 * scale) + reqSize.Width + (int)(24 * scale);

        using (var availBrush = new SolidBrush(isSufficient ? theme.TextMuted : theme.ErrorColor))
        {
            var textToDraw = isSufficient ? availableText : $"{availableText} (空间不足)";
            g.DrawString(textToDraw, theme.SmallFont, availBrush, availX, spaceY);
        }

        // Checkboxes
        var chkShortcutBounds = GetDesktopShortcutCheckBounds(scale);
        var chkMenuBounds = GetContextMenuCheckBounds(scale);
        DrawCheckbox(g, theme, "创建桌面快捷方式", chkShortcutBounds, state.CreateDesktopShortcut, state.HoveredControlId == "chk_shortcut");
        DrawCheckbox(g, theme, "启用桌面右键菜单", chkMenuBounds, state.RegisterContextMenu, state.HoveredControlId == "chk_menu");

        // Action Buttons (clean presentation without separator line)
        var cancelBounds = GetCancelButtonBounds(scale);
        var installBounds = GetInstallButtonBounds(scale);
        DrawButton(g, theme, "取消", cancelBounds, false, state.HoveredControlId == "cancel_btn", state.PressedControlId == "cancel_btn", true);

        var isChecking = state.IsCheckingDependencies;
        var defaultBtnText = state.IsUpgrade ? "立即更新" : "立即安装";
        var installText = isChecking ? (state.IsUpgrade ? "检查更新环境中..." : "环境检测中...") : defaultBtnText;
        DrawButton(g, theme, installText, installBounds, true, state.HoveredControlId == "install_btn", state.PressedControlId == "install_btn", !isChecking);
    }

    private static void RenderInstallingPage(Graphics g, InstallerTheme theme, InstallerState state)
    {
        var scale = theme.DpiScale;
        var winW = GetScaledWidth(scale);
        var winH = GetScaledHeight(scale);

        // Header
        var headerX = (int)(36 * scale);
        var headerY = (int)(46 * scale);
        var iconSize = (int)(34 * scale);

        LucideInstallerIcons.DrawIcon(g, LucideIcon.Package, new RectangleF(headerX, headerY, iconSize, iconSize), theme.Accent, 30 * scale);

        var titleLeft = headerX + iconSize + (int)(12 * scale);
        var installHeaderTitle = state.IsUpgrade ? "正在升级 CrabDesk..." : "正在安装 CrabDesk...";
        using (var titleBrush = new SolidBrush(theme.TextPrimary))
        {
            g.DrawString(installHeaderTitle, theme.TitleFont, titleBrush, titleLeft, headerY - (int)(1 * scale));
        }

        var installHeaderSubtitle = state.IsUpgrade
            ? "安装程序正在停止旧版本进程、部署最新核心文件并迁移桌面分区..."
            : "安装程序正在配置系统运行环境并部署文件，请稍候...";
        using (var subBrush = new SolidBrush(theme.TextSecondary))
        {
            g.DrawString(installHeaderSubtitle, theme.NormalFont, subBrush, titleLeft, headerY + (int)(26 * scale));
        }

        // Progress Card
        var cardLeft = (int)(36 * scale);
        var cardTop = (int)(116 * scale);
        var cardWidth = winW - (int)(72 * scale);
        var cardHeight = (int)(150 * scale);
        var cardBounds = new Rectangle(cardLeft, cardTop, cardWidth, cardHeight);
        DrawCard(g, theme, cardBounds);

        // Current Action with Refresh/Activity Icon
        var actionIconSize = (int)(16 * scale);
        var actionY = cardTop + (int)(26 * scale);
        LucideInstallerIcons.DrawIconRotated(g, LucideIcon.RefreshCw, new RectangleF(cardLeft + (int)(22 * scale), actionY + (int)(1 * scale), actionIconSize, actionIconSize), theme.Accent, 15 * scale, state.CheckingAnimationAngle);

        using (var actionBrush = new SolidBrush(theme.TextPrimary))
        {
            g.DrawString(state.CurrentAction, theme.ButtonFont, actionBrush, cardLeft + (int)(46 * scale), actionY);
        }

        // Progress Bar
        var barLeft = cardLeft + (int)(22 * scale);
        var barWidth = cardWidth - (int)(44 * scale);
        var barY = cardTop + (int)(100 * scale);
        var barBounds = new Rectangle(barLeft, barY, barWidth, (int)(8 * scale));
        DrawProgressBar(g, theme, barBounds, state.ProgressPercentage);

        // Sub Action (left) & Percentage (right) on the same baseline above progress bar
        var infoY = barY - (int)(24 * scale);

        if (!string.IsNullOrWhiteSpace(state.SubAction))
        {
            using var subActionBrush = new SolidBrush(theme.TextSecondary);
            g.DrawString(state.SubAction, theme.SmallFont, subActionBrush, barLeft, infoY + (int)(2 * scale));
        }

        // Percentage Text (aligned right to progress bar)
        using (var percentBrush = new SolidBrush(theme.Accent))
        {
            var pText = $"{state.ProgressPercentage:F0}%";
            var size = g.MeasureString(pText, theme.ButtonFont);
            g.DrawString(pText, theme.ButtonFont, percentBrush, barBounds.Right - size.Width, infoY);
        }

        // Notice removed
    }

    private static void RenderCompletedPage(Graphics g, InstallerTheme theme, InstallerState state)
    {
        var scale = theme.DpiScale;
        var winW = GetScaledWidth(scale);
        var winH = GetScaledHeight(scale);

        // Big Success Check Icon using Lucide CheckCircle with plenty of headroom to prevent clipping
        var iconBoxSize = (int)(96 * scale);
        var iconCenter = new Point(winW / 2, (int)(102 * scale));
        LucideInstallerIcons.DrawIcon(g, LucideIcon.CheckCircle, new RectangleF(iconCenter.X - iconBoxSize / 2, iconCenter.Y - iconBoxSize / 2, iconBoxSize, iconBoxSize), theme.SuccessColor, 56 * scale);

        // Title
        var completedTitle = state.IsUpgrade ? "CrabDesk 升级完成！" : "CrabDesk 安装完成！";
        using (var titleBrush = new SolidBrush(theme.TextPrimary))
        {
            var size = g.MeasureString(completedTitle, theme.TitleFont);
            g.DrawString(completedTitle, theme.TitleFont, titleBrush, (winW - size.Width) / 2, (int)(155 * scale));
        }

        var completedDesc = state.IsUpgrade
            ? $"已成功升级至版本 v{state.Version}，您的桌面分区与配置已完整保留。"
            : "运行环境与桌面右键菜单已全部就绪。";
        using (var descBrush = new SolidBrush(theme.TextSecondary))
        {
            var size = g.MeasureString(completedDesc, theme.NormalFont);
            g.DrawString(completedDesc, theme.NormalFont, descBrush, (winW - size.Width) / 2, (int)(195 * scale));
        }

        // Checkbox: Launch Now
        var launchBounds = GetLaunchCheckBounds(scale);
        DrawCheckbox(g, theme, "立即启动 CrabDesk", launchBounds, state.LaunchOnFinish, state.HoveredControlId == "chk_launch");

        // Finish Button (clean presentation without separator line)
        var finishBounds = GetFinishButtonBounds(scale);
        DrawButton(g, theme, "完成", finishBounds, true, state.HoveredControlId == "finish_btn", state.PressedControlId == "finish_btn", true);
    }

    private static void RenderErrorPage(Graphics g, InstallerTheme theme, InstallerState state)
    {
        var scale = theme.DpiScale;
        var winW = GetScaledWidth(scale);
        var winH = GetScaledHeight(scale);

        var iconSize = (int)(56 * scale);
        var iconCenter = new Point(winW / 2, (int)(90 * scale));
        LucideInstallerIcons.DrawIcon(g, LucideIcon.CircleAlert, new RectangleF(iconCenter.X - iconSize / 2, iconCenter.Y - iconSize / 2, iconSize, iconSize), theme.ErrorColor, 52 * scale);

        using (var titleBrush = new SolidBrush(theme.TextPrimary))
        {
            var title = "安装遇到问题";
            var size = g.MeasureString(title, theme.TitleFont);
            g.DrawString(title, theme.TitleFont, titleBrush, (winW - size.Width) / 2, (int)(135 * scale));
        }

        // Error message card
        var cardLeft = (int)(50 * scale);
        var cardTop = (int)(175 * scale);
        var cardWidth = winW - (int)(100 * scale);
        var cardHeight = (int)(150 * scale);
        var cardBounds = new Rectangle(cardLeft, cardTop, cardWidth, cardHeight);
        DrawCard(g, theme, cardBounds);

        using (var errTextBrush = new SolidBrush(theme.ErrorColor))
        {
            var rect = new RectangleF(cardLeft + (int)(16 * scale), cardTop + (int)(14 * scale), cardWidth - (int)(32 * scale), cardHeight - (int)(28 * scale));
            g.DrawString(state.ErrorMessage ?? "未知错误", theme.SmallFont, errTextBrush, rect);
        }

        // Action Buttons (clean presentation without separator line)
        var retryBounds = GetRetryButtonBounds(scale);
        var closeBounds = GetErrorCloseButtonBounds(scale);
        DrawButton(g, theme, "重试", retryBounds, true, state.HoveredControlId == "retry_btn", state.PressedControlId == "retry_btn", true);
        DrawButton(g, theme, "关闭", closeBounds, false, state.HoveredControlId == "error_close_btn", state.PressedControlId == "error_close_btn", true);
    }

    public static void DrawButton(Graphics g, InstallerTheme theme, string text, Rectangle bounds, bool isPrimary, bool isHovered, bool isPressed, bool isEnabled)
    {
        var scale = theme.DpiScale;
        Color bgColor;
        Color textColor;
        if (!isEnabled)
        {
            bgColor = isPrimary ? Color.FromArgb(110, theme.Accent) : Color.FromArgb(110, theme.ButtonSecondaryBackground);
            textColor = isPrimary ? Color.FromArgb(180, Color.White) : theme.TextMuted;
        }
        else if (isPrimary)
        {
            bgColor = isPressed ? theme.AccentPressed : (isHovered ? theme.AccentHover : theme.Accent);
            textColor = Color.White;
        }
        else
        {
            bgColor = isPressed ? theme.ButtonSecondaryBackground : (isHovered ? theme.ButtonSecondaryHover : theme.ButtonSecondaryBackground);
            textColor = theme.TextPrimary;
        }

        using (var brush = new SolidBrush(bgColor))
        using (var borderPen = new Pen(isPrimary ? Color.Transparent : theme.BorderColor, 1))
        {
            FillRoundedRectangle(g, brush, bounds, (int)(5 * scale));
            if (!isPrimary)
            {
                DrawRoundedRectangle(g, borderPen, bounds, (int)(5 * scale));
            }
        }

        using (var textBrush = new SolidBrush(textColor))
        {
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(text, theme.ButtonFont, textBrush, bounds, format);
        }
    }

    public static void DrawProgressBar(Graphics g, InstallerTheme theme, Rectangle bounds, double percentage)
    {
        using (var trackBrush = new SolidBrush(theme.ProgressBarTrack))
        {
            FillRoundedRectangle(g, trackBrush, bounds, bounds.Height / 2);
        }

        var fillWidth = (int)(bounds.Width * Math.Clamp(percentage / 100.0, 0.0, 1.0));
        if (fillWidth > 4)
        {
            var fillRect = new Rectangle(bounds.X, bounds.Y, fillWidth, bounds.Height);
            using var fillBrush = new SolidBrush(theme.Accent);
            FillRoundedRectangle(g, fillBrush, fillRect, bounds.Height / 2);
        }
    }

    public static void DrawCard(Graphics g, InstallerTheme theme, Rectangle bounds)
    {
        var scale = theme.DpiScale;
        using var cardBg = new SolidBrush(theme.CardBackground);
        using var cardBorder = new Pen(theme.BorderColor, 1);
        FillRoundedRectangle(g, cardBg, bounds, (int)(6 * scale));
        DrawRoundedRectangle(g, cardBorder, bounds, (int)(6 * scale));
    }

    public static void DrawBadge(Graphics g, InstallerTheme theme, string text, Rectangle bounds, Color textColor, Color bgColor)
    {
        var scale = theme.DpiScale;
        using (var brush = new SolidBrush(bgColor))
        {
            FillRoundedRectangle(g, brush, bounds, (int)(4 * scale));
        }

        using (var textBrush = new SolidBrush(textColor))
        {
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(text, theme.BadgeFont, textBrush, bounds, format);
        }
    }

    public static void DrawCheckbox(Graphics g, InstallerTheme theme, string text, Rectangle bounds, bool isChecked, bool isHovered)
    {
        var scale = theme.DpiScale;
        var boxSize = (int)(16 * scale);
        var boxRect = new Rectangle(bounds.X, bounds.Y + (bounds.Height - boxSize) / 2, boxSize, boxSize);

        Color boxBg = isChecked ? theme.Accent : (isHovered ? theme.ButtonSecondaryHover : theme.CardBackground);
        Color boxBorder = isChecked ? theme.Accent : theme.BorderColor;

        using (var bgBrush = new SolidBrush(boxBg))
        using (var borderPen = new Pen(boxBorder, 1.2f))
        {
            FillRoundedRectangle(g, bgBrush, boxRect, (int)(3 * scale));
            DrawRoundedRectangle(g, borderPen, boxRect, (int)(3 * scale));
        }

        if (isChecked)
        {
            LucideInstallerIcons.DrawIcon(g, LucideIcon.Check, new RectangleF(boxRect.X, boxRect.Y + 1, boxRect.Width, boxRect.Height), Color.White, 12 * scale);
        }

        using (var textBrush = new SolidBrush(theme.TextPrimary))
        {
            var format = new StringFormat
            {
                LineAlignment = StringAlignment.Center
            };
            var textRect = new RectangleF(bounds.X + boxSize + (int)(8 * scale), bounds.Y, bounds.Width - boxSize - (int)(8 * scale), bounds.Height);
            g.DrawString(text, theme.NormalFont, textBrush, textRect, format);
        }
    }

    public static void FillRoundedRectangle(Graphics g, Brush brush, Rectangle bounds, int radius)
    {
        using var path = CreateRoundedRectanglePath(bounds, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(Graphics g, Pen pen, Rectangle bounds, int radius)
    {
        using var path = CreateRoundedRectanglePath(bounds, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        // Top-left
        path.AddArc(arc, 180, 90);

        // Top-right
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);

        // Bottom-right
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        // Bottom-left
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }
}
