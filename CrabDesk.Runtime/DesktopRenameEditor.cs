using System.Drawing;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

/// <summary>
/// In-place label editor that mirrors Explorer's rename box: a small
/// borderless input shown over the icon label, with the file-name stem
/// pre-selected. Enter commits, Esc cancels, and losing focus commits.
/// </summary>
internal sealed class DesktopRenameEditor : Forms.Form
{
    private const int BorderPixels = 1;
    private const int WsExToolWindow = 0x00000080;
    private readonly Forms.TextBox _input;
    private TaskCompletionSource<string?>? _completion;
    private bool _finished;

    internal DesktopRenameEditor()
    {
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        Padding = new Forms.Padding(BorderPixels);
        _input = new Forms.TextBox
        {
            BorderStyle = Forms.BorderStyle.None,
            Dock = Forms.DockStyle.Fill,
            AutoSize = false,
            Multiline = true,
            ScrollBars = Forms.ScrollBars.None,
            WordWrap = true,
            AcceptsReturn = false,
            TextAlign = Forms.HorizontalAlignment.Center,
            TabStop = true
        };
        _input.KeyDown += OnInputKeyDown;
        _input.Leave += OnInputLeave;
        Controls.Add(_input);
        // Clicking another application's window commits the edit, matching
        // Explorer. Clicks on the desktop surfaces themselves are handled by
        // the owning surface via CommitExternally, because those windows
        // never activate (WM_MOUSEACTIVATE MA_NOACTIVATE).
        Deactivate += (_, _) => Complete(commit: true);
    }

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    internal bool IsActive => _completion is not null && !_finished;

    internal Task<string?> ShowAsync(
        Point screenLocation,
        Size size,
        string initialText,
        bool selectNameStem,
        bool isDarkTheme,
        Font labelFont,
        bool wordWrap = true)
    {
        if (_completion is not null)
        {
            return Task.FromResult<string?>(null);
        }

        BackColor = isDarkTheme ? Color.FromArgb(102, 102, 102) : Color.FromArgb(150, 150, 150);
        _input.BackColor = isDarkTheme ? Color.FromArgb(50, 50, 50) : Color.White;
        _input.ForeColor = isDarkTheme ? Color.FromArgb(245, 245, 245) : Color.FromArgb(31, 31, 31);
        _input.Font = labelFont ?? new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _input.Multiline = wordWrap;
        _input.WordWrap = wordWrap;
        // The caller already converts the label's DIP geometry to monitor
        // pixels. Create the PerMonitorV2 handle before applying those bounds
        // so WinForms does not scale the first rename window a second time.
        EnsureHandle();
        var requestedBounds = new Rectangle(screenLocation, size);
        Bounds = requestedBounds;
        _input.Text = initialText;
        _input.TextAlign = Forms.HorizontalAlignment.Center;
        _completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _finished = false;
        Show();
        // Reapply after Show in case the target monitor completed its first
        // DPI negotiation while the hidden tool window became visible.
        Bounds = requestedBounds;
        Activate();
        _input.Focus();
        DiagnosticLog.Info(
            $"Rename editor shown requested={requestedBounds} actual={Bounds} dpi={DeviceDpi} wrap={wordWrap}");
        SelectStem(initialText, selectNameStem);
        return _completion.Task;
    }

    private void EnsureHandle()
    {
        if (!IsDisposed && !IsHandleCreated)
        {
            CreateControl();
        }
    }

    internal static float CalculateEditorWidth(
        float labelLayoutWidth,
        float availableWidth)
    {
        var available = Math.Max(1f, availableWidth);
        return Math.Min(available, Math.Max(1f, labelLayoutWidth));
    }

    internal static float CalculateEditorHeight(
        float wrappedTextHeight,
        float lineHeight,
        float availableHeight)
    {
        var available = Math.Max(1f, availableHeight);
        var contentHeight = Math.Max(Math.Max(1f, lineHeight), wrappedTextHeight);
        return Math.Min(available, contentHeight + BorderPixels * 2);
    }

    /// <summary>
    /// Commits the pending edit because the pointer clicked a desktop surface
    /// (which never activates, so the normal Deactivate path is not hit).
    /// </summary>
    internal void CommitExternally()
    {
        if (IsActive)
        {
            Complete(commit: true);
        }
    }

    private void SelectStem(string text, bool selectNameStem)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!selectNameStem)
        {
            _input.SelectAll();
            return;
        }

        // Explorer pre-selects the file name without its final extension
        // (e.g. "report" in "report.pdf", "a.b" in "a.b.txt").
        var dot = text.LastIndexOf('.');
        if (dot > 0 && dot < text.Length - 1)
        {
            _input.Select(0, dot);
        }
        else
        {
            _input.SelectAll();
        }
    }

    private void OnInputKeyDown(object? sender, Forms.KeyEventArgs eventArgs)
    {
        // During IME composition the Enter that confirms the composition
        // arrives as a ProcessKey; only a real Enter commits the edit.
        if (eventArgs.KeyCode == Forms.Keys.Enter && !eventArgs.SuppressKeyPress)
        {
            eventArgs.SuppressKeyPress = true;
            Complete(commit: true);
        }
        else if (eventArgs.KeyCode == Forms.Keys.Escape)
        {
            eventArgs.SuppressKeyPress = true;
            Complete(commit: false);
        }
    }

    private void OnInputLeave(object? sender, EventArgs eventArgs)
    {
        if (!_finished)
        {
            Complete(commit: true);
        }
    }

    private void Complete(bool commit)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        var completion = _completion;
        _completion = null;
        Hide();
        if (completion is null)
        {
            return;
        }

        if (!commit)
        {
            completion.TrySetResult(null);
            return;
        }

        var newName = (_input.Text ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Trim();
        // An empty name is treated as a cancelled edit; the owning surface
        // no-ops when the committed name equals the current one.
        completion.TrySetResult(newName.Length == 0 ? null : newName);
    }
}
