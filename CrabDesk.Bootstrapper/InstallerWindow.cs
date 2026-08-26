using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace CrabDesk.Bootstrapper;

internal sealed class InstallerWindow
{
    private const string WindowClassName = "CrabDesk_Modern_Installer_Window";

    // Win32 Constants
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_EX_APPWINDOW = 0x00040000;

    private const int WS_CHILD = 0x40000000;
    private const int WS_TABSTOP = 0x00010000;
    private const int ES_AUTOHSCROLL = 0x0080;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_SETFONT = 0x0030;
    private const uint WM_SETICON = 0x0080;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CTLCOLOREDIT = 0x0133;
    private const uint WM_CTLCOLORSTATIC = 0x0138;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_USER_REFRESH = 0x0400 + 101;
    private const uint WM_USER_OPEN_BROWSE = 0x0400 + 102;
    private const uint EM_SETSEL = 0x00B1;

    private const int TIMER_ANIMATION = 101;

    private const int EN_SETFOCUS = 0x0100;
    private const int EN_CHANGE = 0x0300;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;

    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private readonly IntPtr _hwnd;
    private readonly InstallerTheme _theme;
    private readonly InstallerState _state;
    private readonly IReadOnlyDictionary<string, string> _metadata;
    private readonly IReadOnlyList<SetupDependency> _dependencies;
    private readonly string[] _rawArguments;
    private readonly HttpClient _client;

    private IntPtr _hwndPathEdit = IntPtr.Zero;
    private IntPtr _hbrEditBg = IntPtr.Zero;
    private IntPtr _hFontEdit = IntPtr.Zero;
    private IntPtr _hBigIcon = IntPtr.Zero;
    private IntPtr _hSmallIcon = IntPtr.Zero;

    private BufferedGraphicsContext _graphicsContext;
    private BufferedGraphics? _bufferedGraphics;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly WndProcDelegate _wndProcDelegate;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public InstallerWindow(
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyList<SetupDependency> dependencies,
        string[] rawArguments,
        HttpClient client)
    {
        _metadata = metadata;
        _dependencies = dependencies;
        _rawArguments = rawArguments;
        _client = client;

        var dpiScale = GetSystemDpiScale();
        _theme = new InstallerTheme(dpiScale);
        _state = new InstallerState
        {
            Version = metadata.GetValueOrDefault("ReleaseVersion", "20260826.02"),
            IsCheckingDependencies = true
        };

        // Initialize dependencies to Pending status for animation
        foreach (var dep in _dependencies)
        {
            _state.Dependencies.Add(new DependencyItemState
            {
                Dependency = dep,
                Status = DependencyCheckStatus.Pending,
                StatusText = "等待检测"
            });
        }

        _graphicsContext = BufferedGraphicsManager.Current;
        _wndProcDelegate = CustomWndProc;

        _hwnd = InitializeNativeWindow();

        // Start progressive animation and checking workflow
        SetTimer(_hwnd, (IntPtr)TIMER_ANIMATION, 25, IntPtr.Zero);
        StartDependencyDetectionWorkflow();
    }

    public static int Run(
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyList<SetupDependency> dependencies,
        string[] args,
        HttpClient client)
    {
        var window = new InstallerWindow(metadata, dependencies, args, client);
        return window.RunMessageLoop();
    }

    private IntPtr InitializeNativeWindow()
    {
        var hInstance = GetModuleHandleW(IntPtr.Zero);

        LoadApplicationIcons(out _hBigIcon, out _hSmallIcon);

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0x0002 | 0x0001, // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            hIcon = _hBigIcon,
            hIconSm = _hSmallIcon,
            hCursor = LoadCursorW(IntPtr.Zero, 32512), // IDC_ARROW
            lpszClassName = WindowClassName
        };
        RegisterClassExW(ref wndClass);

        var scale = _theme.DpiScale;
        var winW = InstallerRenderer.GetScaledWidth(scale);
        var winH = InstallerRenderer.GetScaledHeight(scale);

        var screenWidth = GetSystemMetrics(0);
        var screenHeight = GetSystemMetrics(1);
        var x = Math.Max(0, (screenWidth - winW) / 2);
        var y = Math.Max(0, (screenHeight - winH) / 2);

        var hwnd = CreateWindowExW(
            WS_EX_APPWINDOW,
            WindowClassName,
            "CrabDesk 安装向导",
            WS_POPUP | WS_VISIBLE | WS_MINIMIZEBOX | WS_SYSMENU,
            x,
            y,
            winW,
            winH,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法创建安装器主窗口。");
        }

        // Apply Icons to Window & Taskbar
        if (_hBigIcon != IntPtr.Zero)
        {
            SendMessageW(hwnd, WM_SETICON, (IntPtr)ICON_BIG, _hBigIcon);
        }
        if (_hSmallIcon != IntPtr.Zero)
        {
            SendMessageW(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, _hSmallIcon);
        }

        // Apply DWM Dark Mode & Round Corners
        int darkMode = _theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        int cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        // Create Path Edit Control positioned inside path box
        var pathBox = InstallerRenderer.GetPathBoxBounds(scale);
        _hbrEditBg = CreateSolidBrush(ColorTranslator.ToWin32(_theme.CardBackground));
        _hFontEdit = _theme.NormalFont.ToHfont();

        var editPadX = (int)(8 * scale);
        var editPadY = (int)(6 * scale);
        _hwndPathEdit = CreateWindowExW(
            0,
            "EDIT",
            _state.InstallPath,
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
            pathBox.X + editPadX,
            pathBox.Y + editPadY,
            pathBox.Width - editPadX * 2,
            pathBox.Height - editPadY * 2,
            hwnd,
            (IntPtr)1001,
            hInstance,
            IntPtr.Zero);

        if (_hwndPathEdit != IntPtr.Zero && _hFontEdit != IntPtr.Zero)
        {
            SendMessageW(_hwndPathEdit, WM_SETFONT, _hFontEdit, new IntPtr(1));
        }

        ShowWindow(hwnd, SW_SHOW);
        UpdateWindow(hwnd);

        return hwnd;
    }

    public int RunMessageLoop()
    {
        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return (int)msg.wParam;
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1);

            case WM_PAINT:
                PaintWindow(hWnd);
                return IntPtr.Zero;

            case WM_NCHITTEST:
                var pt = GetPointFromLParam(lParam);
                var clientPt = ScreenToClientPoint(hWnd, pt);
                var tbHeight = InstallerRenderer.GetScaledTitleBarHeight(_theme.DpiScale);

                if (clientPt.Y >= 0 && clientPt.Y <= tbHeight)
                {
                    var closeBounds = InstallerRenderer.GetCloseButtonBounds(_theme.DpiScale);
                    var minBounds = InstallerRenderer.GetMinButtonBounds(_theme.DpiScale);
                    if (closeBounds.Contains(clientPt) || minBounds.Contains(clientPt))
                    {
                        return new IntPtr(HTCLIENT);
                    }
                    return new IntPtr(HTCAPTION);
                }
                return new IntPtr(HTCLIENT);

            case WM_CTLCOLOREDIT:
            case WM_CTLCOLORSTATIC:
                if (lParam == _hwndPathEdit)
                {
                    SetTextColor(wParam, ColorTranslator.ToWin32(_theme.TextPrimary));
                    SetBkColor(wParam, ColorTranslator.ToWin32(_theme.CardBackground));
                    return _hbrEditBg;
                }
                break;

            case WM_COMMAND:
                var controlId = (uint)wParam & 0xFFFF;
                var notifyCode = (uint)wParam >> 16;
                if (controlId == 1001)
                {
                    if (notifyCode == EN_CHANGE)
                    {
                        var len = GetWindowTextLengthW(_hwndPathEdit);
                        var sb = new StringBuilder(len + 1);
                        GetWindowTextW(_hwndPathEdit, sb, sb.Capacity);
                        _state.InstallPath = sb.ToString();
                        Invalidate();
                    }
                    else if (notifyCode == EN_SETFOCUS)
                    {
                        PostMessageW(_hwndPathEdit, EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
                    }
                }
                break;

            case WM_MOUSEMOVE:
                var mousePt = GetPointFromLParam(lParam);
                HandleMouseMove(mousePt);
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                var downPt = GetPointFromLParam(lParam);
                HandleMouseDown(downPt);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                var upPt = GetPointFromLParam(lParam);
                HandleMouseUp(upPt);
                return IntPtr.Zero;

            case WM_TIMER:
                if (wParam == (IntPtr)TIMER_ANIMATION)
                {
                    _state.CheckingAnimationAngle = (_state.CheckingAnimationAngle + 12f) % 360f;

                    if (_state.Page == InstallerPage.Installing)
                    {
                        var diff = _state.TargetProgressPercentage - _state.ProgressPercentage;
                        if (Math.Abs(diff) > 0.05)
                        {
                            _state.ProgressPercentage += diff * 0.15;
                        }
                        else
                        {
                            _state.ProgressPercentage = _state.TargetProgressPercentage;
                        }
                    }

                    Invalidate();
                    return IntPtr.Zero;
                }
                break;

            case WM_USER_REFRESH:
                Invalidate();
                return IntPtr.Zero;

            case WM_USER_OPEN_BROWSE:
                OpenFolderBrowserAsync();
                return IntPtr.Zero;

            case WM_CLOSE:
                if (_state.Page == InstallerPage.Installing)
                {
                    var result = MessageBoxW(
                        hWnd,
                        "安装正在进行中，确定要取消安装并退出吗？",
                        "CrabDesk 安装向导",
                        0x00000001 | 0x00000030); // MB_OKCANCEL | MB_ICONWARNING
                    if (result != 1)
                    {
                        return IntPtr.Zero;
                    }
                    _cancellationTokenSource?.Cancel();
                }
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                KillTimer(hWnd, (IntPtr)TIMER_ANIMATION);
                if (_hbrEditBg != IntPtr.Zero) DeleteObject(_hbrEditBg);
                if (_hFontEdit != IntPtr.Zero) DeleteObject(_hFontEdit);
                if (_hBigIcon != IntPtr.Zero) DestroyIcon(_hBigIcon);
                if (_hSmallIcon != IntPtr.Zero) DestroyIcon(_hSmallIcon);
                _bufferedGraphics?.Dispose();
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void PaintWindow(IntPtr hWnd)
    {
        var hdc = BeginPaint(hWnd, out var ps);
        try
        {
            var winW = InstallerRenderer.GetScaledWidth(_theme.DpiScale);
            var winH = InstallerRenderer.GetScaledHeight(_theme.DpiScale);

            if (_bufferedGraphics == null)
            {
                _bufferedGraphics = _graphicsContext.Allocate(
                    hdc,
                    new Rectangle(0, 0, winW, winH));
            }

            if (_hwndPathEdit != IntPtr.Zero)
            {
                var len = GetWindowTextLengthW(_hwndPathEdit);
                if (len > 0)
                {
                    var sb = new StringBuilder(len + 1);
                    GetWindowTextW(_hwndPathEdit, sb, sb.Capacity);
                    var text = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _state.InstallPath = text;
                    }
                }
            }

            InstallerRenderer.Render(_bufferedGraphics.Graphics, _theme, _state);
            _bufferedGraphics.Render(hdc);
        }
        finally
        {
            EndPaint(hWnd, ref ps);
        }
    }

    private void Invalidate()
    {
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void HandleMouseMove(Point pt)
    {
        var prevHovered = _state.HoveredControlId;
        _state.HoveredControlId = HitTest(pt);

        if (prevHovered != _state.HoveredControlId)
        {
            Invalidate();
        }
    }

    private void HandleMouseDown(Point pt)
    {
        _state.PressedControlId = HitTest(pt);
        Invalidate();
    }

    private void HandleMouseUp(Point pt)
    {
        var controlId = _state.PressedControlId;
        _state.PressedControlId = null;

        if (controlId != null && controlId == HitTest(pt))
        {
            TriggerControlAction(controlId);
        }
        Invalidate();
    }

    private string? HitTest(Point pt)
    {
        var scale = _theme.DpiScale;
        if (InstallerRenderer.GetCloseButtonBounds(scale).Contains(pt)) return "close_btn";
        if (InstallerRenderer.GetMinButtonBounds(scale).Contains(pt)) return "min_btn";

        switch (_state.Page)
        {
            case InstallerPage.Welcome:
                if (InstallerRenderer.GetBrowseButtonBounds(scale).Contains(pt)) return "browse_btn";
                if (InstallerRenderer.GetDesktopShortcutCheckBounds(scale).Contains(pt)) return "chk_shortcut";
                if (InstallerRenderer.GetContextMenuCheckBounds(scale).Contains(pt)) return "chk_menu";
                if (InstallerRenderer.GetCancelButtonBounds(scale).Contains(pt)) return "cancel_btn";
                if (InstallerRenderer.GetInstallButtonBounds(scale).Contains(pt)) return _state.IsCheckingDependencies ? null : "install_btn";
                break;

            case InstallerPage.Completed:
                if (InstallerRenderer.GetLaunchCheckBounds(scale).Contains(pt)) return "chk_launch";
                if (InstallerRenderer.GetFinishButtonBounds(scale).Contains(pt)) return "finish_btn";
                break;

            case InstallerPage.Error:
                if (InstallerRenderer.GetRetryButtonBounds(scale).Contains(pt)) return "retry_btn";
                if (InstallerRenderer.GetErrorCloseButtonBounds(scale).Contains(pt)) return "error_close_btn";
                break;
        }

        return null;
    }

    private void TriggerControlAction(string controlId)
    {
        switch (controlId)
        {
            case "close_btn":
            case "cancel_btn":
            case "error_close_btn":
                PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;

            case "min_btn":
                ShowWindow(_hwnd, SW_MINIMIZE);
                break;

            case "browse_btn":
                PostMessageW(_hwnd, WM_USER_OPEN_BROWSE, IntPtr.Zero, IntPtr.Zero);
                break;

            case "chk_shortcut":
                _state.CreateDesktopShortcut = !_state.CreateDesktopShortcut;
                break;

            case "chk_menu":
                _state.RegisterContextMenu = !_state.RegisterContextMenu;
                break;

            case "chk_launch":
                _state.LaunchOnFinish = !_state.LaunchOnFinish;
                break;

            case "install_btn":
            case "retry_btn":
                if (!_state.IsCheckingDependencies)
                {
                    StartInstallation();
                }
                break;

            case "finish_btn":
                if (_state.LaunchOnFinish)
                {
                    TryLaunchInstalledApp();
                }
                PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
        }
    }

    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IidFileOpenDialog = new("D2BE3340-982F-4A42-BB0E-8F0E16D2F0EE");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private void OpenFolderBrowserAsync()
    {
        if (_hwndPathEdit != IntPtr.Zero)
        {
            var len = GetWindowTextLengthW(_hwndPathEdit);
            if (len > 0)
            {
                var sb = new StringBuilder(len + 1);
                GetWindowTextW(_hwndPathEdit, sb, sb.Capacity);
                var text = sb.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _state.InstallPath = text;
                }
            }
        }

        var currentPath = _state.InstallPath;

        var thread = new Thread(() =>
        {
            OleInitialize(IntPtr.Zero);
            string? chosenPath = null;

            try
            {
                var clsid = ClsidFileOpenDialog;
                var iid = IidFileOpenDialog;
                var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var ppv);
                if (hr == 0 && ppv != IntPtr.Zero)
                {
                    var dialog = (IFileOpenDialog)Marshal.GetObjectForIUnknown(ppv);
                    dialog.SetTitle("选择 CrabDesk 安装目录");
                    dialog.SetOptions(0x0020 | 0x0040); // FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
                    dialog.ClearClientData();

                    var initialPath = currentPath;
                    var cleanPath = initialPath.Trim().Trim('"', '\'').Trim();
                    var parentDir = Path.GetDirectoryName(cleanPath);

                    string? lookupPath = null;
                    if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
                    {
                        lookupPath = parentDir;
                    }
                    else if (Directory.Exists(cleanPath))
                    {
                        lookupPath = cleanPath;
                    }
                    else
                    {
                        var root = Path.GetPathRoot(cleanPath);
                        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                        {
                            lookupPath = root;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(lookupPath) && Directory.Exists(lookupPath))
                    {
                        var shellItemIid = IidShellItem;
                        if (SHCreateItemFromParsingName(lookupPath, IntPtr.Zero, ref shellItemIid, out var itemPtr) == 0 && itemPtr != IntPtr.Zero)
                        {
                            var folderItem = (IShellItem)Marshal.GetObjectForIUnknown(itemPtr);
                            dialog.SetFolder(folderItem);
                            dialog.SetDefaultFolder(folderItem);
                            Marshal.Release(itemPtr);
                        }
                    }

                    if (dialog.Show(IntPtr.Zero) == 0) // S_OK
                    {
                        dialog.GetResult(out var item);
                        if (item != null)
                        {
                            item.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out var path);
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                chosenPath = SetupPolicy.EnsureAppFolder(path);
                            }
                        }
                    }
                    Marshal.Release(ppv);
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(chosenPath))
            {
                try
                {
                    var bi = new BROWSEINFO
                    {
                        hwndOwner = IntPtr.Zero,
                        lpszTitle = "请选择 CrabDesk 安装目录：",
                        ulFlags = 0x00000001 | 0x00000040 // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE
                    };
                    var pidl = SHBrowseForFolderW(ref bi);
                    if (pidl != IntPtr.Zero)
                    {
                        var pathChars = new char[260];
                        if (SHGetPathFromIDListW(pidl, pathChars))
                        {
                            var selected = new string(pathChars).TrimEnd('\0');
                            if (!string.IsNullOrWhiteSpace(selected))
                            {
                                chosenPath = SetupPolicy.EnsureAppFolder(selected);
                            }
                        }
                        CoTaskMemFree(pidl);
                    }
                }
                catch
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(chosenPath))
            {
                chosenPath = SetupPolicy.EnsureAppFolder(chosenPath);
                _state.InstallPath = chosenPath;
                if (_hwndPathEdit != IntPtr.Zero)
                {
                    SetWindowTextW(_hwndPathEdit, chosenPath);
                }
                PostMessageW(_hwnd, WM_USER_REFRESH, IntPtr.Zero, IntPtr.Zero);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private void StartDependencyDetectionWorkflow()
    {
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200); // Brief initial pause for window show

                foreach (var item in _state.Dependencies)
                {
                    item.Status = DependencyCheckStatus.Checking;
                    item.StatusText = "检测中...";
                    PostMessageW(_hwnd, WM_USER_REFRESH, IntPtr.Zero, IntPtr.Zero);

                    // Allow smooth animated rotation for ~300ms
                    await Task.Delay(280);

                    var isInstalled = DependencyDetector.IsInstalled(item.Dependency);
                    item.Status = isInstalled ? DependencyCheckStatus.Installed : DependencyCheckStatus.Missing;
                    item.StatusText = isInstalled ? "已就绪 ✓" : "需自动下载";
                    PostMessageW(_hwnd, WM_USER_REFRESH, IntPtr.Zero, IntPtr.Zero);

                    await Task.Delay(120);
                }
            }
            catch
            {
                foreach (var item in _state.Dependencies)
                {
                    item.Status = DependencyCheckStatus.Installed;
                    item.StatusText = "已就绪 ✓";
                }
            }
            finally
            {
                _state.IsCheckingDependencies = false;
                KillTimer(_hwnd, (IntPtr)TIMER_ANIMATION);
                PostMessageW(_hwnd, WM_USER_REFRESH, IntPtr.Zero, IntPtr.Zero);
            }
        });
    }

    private void StartInstallation()
    {
        _state.InstallPath = SetupPolicy.EnsureAppFolder(_state.InstallPath);
        _state.Page = InstallerPage.Installing;
        _state.ProgressPercentage = 0;
        _state.TargetProgressPercentage = 0;
        _state.CurrentAction = "正在准备安装环境与解压核心载荷...";
        _state.SubAction = "初始化安装环境并配置运行权限...";

        if (_hwndPathEdit != IntPtr.Zero)
        {
            ShowWindow(_hwndPathEdit, SW_HIDE);
        }
        Invalidate();

        // Ensure 50 FPS timer is running for smooth animation and progress lerping
        SetTimer(_hwnd, (IntPtr)TIMER_ANIMATION, 20, IntPtr.Zero);

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        Task.Run(async () =>
        {
            try
            {
                await ExecuteInstallationWorkflowAsync(token);

                // Wait for progress bar to smoothly reach 100%
                while (_state.ProgressPercentage < 98.0)
                {
                    await Task.Delay(25, token);
                }
                await Task.Delay(350, token);

                _state.Page = InstallerPage.Completed;
            }
            catch (OperationCanceledException)
            {
                _state.ErrorMessage = "用户已取消安装。";
                _state.Page = InstallerPage.Error;
            }
            catch (Exception ex)
            {
                _state.ErrorMessage = $"安装失败：{ex.Message}";
                _state.Page = InstallerPage.Error;
            }
            finally
            {
                KillTimer(_hwnd, (IntPtr)TIMER_ANIMATION);
                PostMessageW(_hwnd, WM_USER_REFRESH, IntPtr.Zero, IntPtr.Zero);
            }
        }, token);
    }

    private async Task ExecuteInstallationWorkflowAsync(CancellationToken token)
    {
        var missing = _dependencies
            .Where(dep => !DependencyDetector.IsInstalled(dep))
            .ToArray();

        var tempRoot = Path.Combine(Path.GetTempPath(), "CrabDesk-Setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            _state.TargetProgressPercentage = 12.0;
            _state.CurrentAction = "正在准备安装环境并关闭运行中的实例...";
            _state.SubAction = "释放文件占用以确保无缝覆盖安装...";
            SetupPolicy.StopRunningAppInstances(_state.InstallPath);
            await Task.Delay(260, token);

            // Step 1: Install Missing Prerequisites (if any)
            if (missing.Length > 0)
            {
                var totalSteps = missing.Length + 1;
                var currentStep = 0;

                foreach (var dep in missing)
                {
                    token.ThrowIfCancellationRequested();

                    currentStep++;
                    _state.CurrentAction = $"正在从微软官方下载 {dep.DisplayName}...";
                    _state.SubAction = "正在连接 Microsoft 官方 CDN...";
                    _state.TargetProgressPercentage = (currentStep - 1.0) / totalSteps * 70.0;
                    PostMessageW(_hwnd, WM_USER_REFRESH, IntPtr.Zero, IntPtr.Zero);

                    var depPath = Path.Combine(tempRoot, dep.FileName);

                    var progress = new Progress<DownloadProgressReport>(report =>
                    {
                        var speedText = report.SpeedBytesPerSecond >= 1024 * 1024
                            ? $"{report.SpeedBytesPerSecond / (1024.0 * 1024.0):F1} MB/s"
                            : $"{report.SpeedBytesPerSecond / 1024.0:F0} KB/s";

                        var downloadedMB = report.BytesDownloaded / (1024.0 * 1024.0);
                        var totalMB = (report.TotalBytes ?? report.BytesDownloaded) / (1024.0 * 1024.0);

                        _state.SubAction = $"{downloadedMB:F1} MB / {totalMB:F1} MB ({speedText})";
                        var stepProgress = (currentStep - 1.0 + report.ProgressPercentage / 100.0) / totalSteps * 70.0;
                        _state.TargetProgressPercentage = Math.Clamp(stepProgress, 0.0, 70.0);
                    });

                    await DownloadVerifier.DownloadAsync(_client, dep.DownloadUri, depPath, 512L * 1024 * 1024, progress, token);

                    _state.CurrentAction = $"正在校验 {dep.DisplayName} 签名与哈希...";
                    _state.SubAction = "验证微软官方 Authenticode 数字证书与 SHA-256";

                    DownloadVerifier.VerifySha256(depPath, dep.Sha256);
                    DownloadVerifier.VerifyTrustedMicrosoftSignature(depPath);

                    _state.CurrentAction = $"正在静默安装 {dep.DisplayName}...";
                    _state.SubAction = "可能出现系统权限确认，请稍候...";

                    var exitCode = RunInstallerProcess(depPath, dep.SilentArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), true);
                    if (!SetupPolicy.IsSuccessfulInstallerExitCode(exitCode))
                    {
                        throw new InvalidOperationException($"{dep.DisplayName} 安装失败，退出代码：{exitCode}。");
                    }
                }
            }

            // Step 2: Extract & Verify Payload
            _state.CurrentAction = "正在提取 CrabDesk 核心程序与界面资源...";
            _state.SubAction = "释放应用程序载荷并验证完整性...";
            _state.TargetProgressPercentage = missing.Length > 0 ? 76.0 : 38.0;
            await Task.Delay(260, token);

            var payloadPath = Path.Combine(tempRoot, SetupPolicy.PayloadName);
            await ExtractPayloadFileAsync(payloadPath);
            DownloadVerifier.VerifySha256(payloadPath, _metadata.GetValueOrDefault("PayloadSha256", ""));

            _state.TargetProgressPercentage = missing.Length > 0 ? 86.0 : 62.0;
            await Task.Delay(220, token);

            // Step 3: Run Inno Setup Payload (clean parameter format without inner quotes & elevated for Program Files)
            var cleanDir = SetupPolicy.EnsureAppFolder(_state.InstallPath);
            SetupPolicy.StopRunningAppInstances(cleanDir);

            _state.CurrentAction = "正在部署 CrabDesk 核心文件与资源...";
            _state.SubAction = "配置桌面快捷方式、右键菜单与系统环境...";
            _state.TargetProgressPercentage = missing.Length > 0 ? 94.0 : 85.0;

            var payloadArgs = new List<string> { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/FORCECLOSEAPPLICATIONS" };
            if (!string.IsNullOrWhiteSpace(cleanDir))
            {
                payloadArgs.Add($"/DIR={cleanDir}");
            }
            if (_state.CreateDesktopShortcut)
            {
                payloadArgs.Add("/TASKS=desktopicon");
            }

            var payloadExit = RunInstallerProcess(payloadPath, payloadArgs, elevate: true);
            if (!SetupPolicy.IsSuccessfulInstallerExitCode(payloadExit))
            {
                throw new InvalidOperationException($"CrabDesk 安装过程返回错误代码：{payloadExit}。");
            }

            // Step 4: Finalize
            _state.TargetProgressPercentage = 100.0;
            _state.CurrentAction = "安装完成！";
            _state.SubAction = "正在完成最终环境配置与清理...";
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task ExtractPayloadFileAsync(string destination)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var source = assembly.GetManifestResourceStream("CrabDesk.Payload.exe")
            ?? throw new InvalidOperationException("安装器内部缺少 CrabDesk 安装载荷。");
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        await source.CopyToAsync(target);
        await target.FlushAsync();
    }

    private static int RunInstallerProcess(string path, IEnumerable<string> arguments, bool elevate)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }
        if (elevate)
        {
            startInfo.Verb = "runas";
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(path)}。");
        process.WaitForExit();
        return process.ExitCode;
    }

    private void TryLaunchInstalledApp()
    {
        var exePath = Path.Combine(_state.InstallPath, "CrabDesk.WinUI.exe");
        if (File.Exists(exePath))
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
    }

    private static void LoadApplicationIcons(out IntPtr hBigIcon, out IntPtr hSmallIcon)
    {
        hBigIcon = IntPtr.Zero;
        hSmallIcon = IntPtr.Zero;
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("Assets.CrabDesk.ico");
            if (stream != null)
            {
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes, 0, bytes.Length);

                var tempPath = Path.Combine(Path.GetTempPath(), "CrabDesk-Installer.ico");
                File.WriteAllBytes(tempPath, bytes);

                hBigIcon = LoadImageW(IntPtr.Zero, tempPath, 1 /* IMAGE_ICON */, 48, 48, 0x00000010 /* LR_LOADFROMFILE */);
                hSmallIcon = LoadImageW(IntPtr.Zero, tempPath, 1 /* IMAGE_ICON */, 24, 24, 0x00000010 /* LR_LOADFROMFILE */);
            }
        }
        catch
        {
        }
    }

    private static float GetSystemDpiScale()
    {
        try
        {
            var dpi = GetDpiForSystem();
            if (dpi > 0)
            {
                return dpi / 96.0f;
            }
        }
        catch
        {
        }
        return 1.0f;
    }

    private static Point GetPointFromLParam(IntPtr lParam)
    {
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        return new Point(x, y);
    }

    private static Point ScreenToClientPoint(IntPtr hWnd, Point pt)
    {
        var p = new POINT { X = pt.X, Y = pt.Y };
        ScreenToClient(hWnd, ref p);
        return new Point(p.X, p.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public IntPtr hdc; public bool fErase; public Rectangle rcPaint; public bool fRestore; public bool fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX { public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int X, int Y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int crColor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hdc, int crColor);

    [DllImport("gdi32.dll")]
    private static extern int SetBkColor(IntPtr hdc, int crColor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(IntPtr lpModuleName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, int type);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, [Out] char[] pszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [ComImport]
    [Guid("D2BE3340-982F-4A42-BB0E-8F0E16D2F0EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
