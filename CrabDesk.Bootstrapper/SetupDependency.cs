namespace CrabDesk.Bootstrapper;

internal enum SetupDependencyKind
{
    VisualCppRuntime,
    DotNetDesktopRuntime,
    WindowsAppRuntime
}

internal sealed record SetupDependency(
    SetupDependencyKind Kind,
    string DisplayName,
    Uri DownloadUri,
    string Sha256,
    string FileName,
    string SilentArguments,
    Version? MinimumVersion = null,
    int RequiredMajorVersion = 0,
    string RequiredPackageName = "");

internal sealed record DependencyStatus(SetupDependency Dependency, bool IsInstalled);
