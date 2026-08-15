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
            // A single-line TextBox is height-clamped by WinForms and does
            // not support horizontal centering. Multiline lifts both limits:
            // the editor sizes the form to the label height (one or two
            // lines) and the name stays centered like Explorer's rename box.
            BorderStyle = Forms.BorderStyle.None,
            Dock = Forms.DockStyle.Fill,
            Multiline = true,
            ScrollBars = Forms.ScrollBars.None,
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
        Font labelFont)
    {
        if (_completion is not null)
        {
            return Task.FromResult<string?>(null);
        }

        BackColor = isDarkTheme ? Color.FromArgb(102, 102, 102) : Color.FromArgb(150, 150, 150);
        _input.BackColor = isDarkTheme ? Color.FromArgb(50, 50, 50) : Color.White;
        _input.ForeColor = isDarkTheme ? Color.FromArgb(245, 245, 245) : Color.FromArgb(31, 31, 31);
        _input.Font = labelFont ?? new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        SetBounds(screenLocation.X, screenLocation.Y, size.Width, size.Height);
        _input.Text = initialText;
        _completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _finished = false;
        Show();
        Activate();
        _input.Focus();
        SelectStem(initialText, selectNameStem);
        return _completion.Task;
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
