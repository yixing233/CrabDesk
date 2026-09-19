# Bootstrapper Dependency Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make online and GUI installation reliably detect, download, install, verify, and report all required Windows dependencies.

**Architecture:** Keep dependency metadata and platform-specific detection in `CrabDesk.Bootstrapper`, add version-aware Windows App Runtime detection, and share bounded retry/timeout helpers between GUI and headless workflows. GUI detection and installation will fail closed, re-check each dependency after installation, and surface restart requirements without launching the app prematurely.

**Tech Stack:** .NET 8, C#, Win32 registry/AppX APIs via PowerShell, `HttpClient`, Authenticode/WinTrust, xUnit.

---

### Task 1: Make dependency detection version-aware and fail closed

**Files:**
- Modify: `CrabDesk.Bootstrapper/SetupDependency.cs`
- Modify: `CrabDesk.Bootstrapper/DependencyDetector.cs`
- Modify: `CrabDesk.Bootstrapper/InstallerState.cs`
- Modify: `CrabDesk.Bootstrapper/InstallerWindow.cs:668-702`
- Test: `CrabDesk.Bootstrapper.Tests/BootstrapperTests.cs`

- [x] Add a `MinimumVersion` field for Windows App Runtime metadata and parse it from `Program.BuildDependencies`.
- [x] Make `DependencyDetector.IsInstalled` accept the minimum version for Windows App Runtime and compare the detected package version, not only package name and architecture.
- [x] Replace GUI detection's exception handler that marks every item installed with a `DetectionFailed` state and disable installation until detection succeeds or the user retries.
- [x] Add tests for Windows App Runtime version comparison and for detection failure not being treated as installed.

### Task 2: Verify each dependency after installation and handle restart codes consistently

**Files:**
- Modify: `CrabDesk.Bootstrapper/InstallerWindow.cs:759-867`
- Modify: `CrabDesk.Bootstrapper/Program.cs:80-133`
- Modify: `CrabDesk.Bootstrapper/InstallerState.cs`
- Test: `CrabDesk.Bootstrapper.Tests/BootstrapperTests.cs`

- [x] Add a shared post-install verification path that re-detects each dependency after its installer exits.
- [x] Preserve `3010`/`1641` as successful but mark `RestartRequired`, stop launching CrabDesk when set, and show an explicit restart result in GUI and headless flows.
- [x] Ensure a restart code does not skip verification of later dependencies.
- [x] Add tests for successful/restart/failure exit-code combinations and restart state behavior.

### Task 3: Add bounded download retry and installer process timeout

**Files:**
- Modify: `CrabDesk.Bootstrapper/DownloadVerifier.cs`
- Modify: `CrabDesk.Bootstrapper/InstallerWindow.cs`
- Modify: `CrabDesk.Bootstrapper/Program.cs`
- Test: `CrabDesk.Bootstrapper.Tests/BootstrapperTests.cs`

- [x] Add three-attempt retry with cancellation-aware exponential backoff for transient HTTP/network failures.
- [x] Download into a `.partial` file and atomically move it into place only after the stream completes.
- [x] Add a bounded installer process wait timeout and kill/return a clear error when exceeded.
- [x] Keep hash/signature verification after the final download attempt.
- [x] Add unit tests for retry classification/backoff calculation and timeout policy.

### Task 4: Enforce install-path disk space before starting downloads

**Files:**
- Modify: `CrabDesk.Bootstrapper/InstallerState.cs`
- Modify: `CrabDesk.Bootstrapper/InstallerWindow.cs`
- Modify: `CrabDesk.Bootstrapper/Program.cs`
- Test: `CrabDesk.Bootstrapper.Tests/BootstrapperTests.cs`

- [x] Calculate required space as payload size plus missing dependency download allowance and a safety margin.
- [x] Prevent the GUI install action and headless workflow from starting when the target or temp volume is insufficient.
- [x] Keep the existing informational display but include the blocking reason in the error state.
- [x] Add tests for sufficient and insufficient space decisions.

### Task 5: Run verification and update the plan

**Files:**
- Modify: `docs/superpowers/plans/2026-08-31-bootstrapper-dependency-hardening.md`

- [x] Run `dotnet test CrabDesk.Bootstrapper.Tests/CrabDesk.Bootstrapper.Tests.csproj -c Debug --no-restore`.
- [x] Run `dotnet test CrabDesk.sln -c Debug --no-restore` after stopping any running CrabDesk process that locks build outputs.
- [x] Run `git diff --check` and record any remaining pre-existing warnings.
- [x] Update this plan's checkboxes with the actual verification results.
