# Online Prerequisite Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the self-contained CrabDesk application installer with one public online setup that installs only missing Windows prerequisites and then installs a framework-dependent CrabDesk payload.

**Architecture:** Publish CrabDesk as a `win-x64` framework-dependent application and package it in an internal Inno Setup payload named `CrabDesk-Payload-x64.exe`. Sign and embed that payload into the public `CrabDesk-Setup-x64.exe`, a Native AOT bootstrapper that validates the operating system, detects .NET Desktop Runtime 8, Windows App SDK Runtime 1.8, and Visual C++ x64, downloads only missing prerequisites from Microsoft, verifies every downloaded executable, then extracts and launches its hash-verified embedded payload. GitHub Releases expose only the final setup and its checksum file.

**Tech Stack:** .NET 8, Native AOT, Win32 registry/process APIs, PowerShell release scripts, Inno Setup 6, GitHub Actions.

---

### Task 1: Make runtime detection and download verification testable

**Files:**
- Create: `CrabDesk.Bootstrapper/SetupDependency.cs`
- Create: `CrabDesk.Bootstrapper/DependencyDetector.cs`
- Create: `CrabDesk.Bootstrapper/DownloadVerifier.cs`
- Create: `CrabDesk.Bootstrapper.Tests/CrabDesk.Bootstrapper.Tests.csproj`
- Create: `CrabDesk.Bootstrapper.Tests/BootstrapperTests.cs`
- Modify: `CrabDesk.Bootstrapper/CrabDesk.Bootstrapper.csproj`
- Modify: `CrabDesk.sln`

- [ ] **Step 1: Add failing parser and policy tests**

Test checksum lines with spaces and `*`, semantic runtime directory names such as `8.0.30`, malformed values, supported installer exit codes `0`, `1638`, `1641`, and `3010`, and reject non-HTTPS dependency URLs.

- [ ] **Step 2: Run focused tests and confirm failure**

Run: `dotnet test CrabDesk.Bootstrapper.Tests/CrabDesk.Bootstrapper.Tests.csproj -c Release`

Expected: FAIL because the detector and verification helpers do not exist yet.

- [ ] **Step 3: Implement the minimal helpers**

Implement immutable dependency metadata, registry/filesystem-based .NET and VC++ detection, current-user Windows App Runtime detection through a bounded PowerShell query, streaming SHA-256 downloads, and Microsoft Authenticode trust validation through `WinVerifyTrust`.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test CrabDesk.Bootstrapper.Tests/CrabDesk.Bootstrapper.Tests.csproj -c Release`

Expected: PASS.

### Task 2: Convert the bootstrapper into the only public setup

**Files:**
- Modify: `CrabDesk.Bootstrapper/Program.cs`
- Modify: `CrabDesk.Bootstrapper/CrabDesk.Bootstrapper.csproj`
- Modify: `build/publish-bootstrapper.ps1`
- Create: `build/verify-bootstrapper.ps1`

- [ ] **Step 1: Add bootstrapper command-policy tests**

Cover operating-system validation, dependency ordering, same-release payload URL construction, checksum lookup for `CrabDesk-Payload-x64.exe`, post-install redetection, and reboot exit handling.

- [ ] **Step 2: Implement setup orchestration**

Read version/dependency metadata from assembly attributes; reject non-x64 or Windows builds below `10.0.19041`; download and silently install only unsatisfied prerequisites; accept `0`, `1638`, `1641`, and `3010`; extract and verify the embedded `CrabDesk-Payload-x64.exe`; run the payload silently; launch CrabDesk when no reboot is pending; and always clean the setup-owned temporary directory.

- [ ] **Step 3: Publish dependency metadata**

Have `publish-bootstrapper.ps1` derive the Windows App SDK version from `CrabDesk.WinUI.csproj`, validate fixed official Microsoft HTTPS installer URLs and hashes, embed the signed payload and its SHA-256 as MSBuild inputs, retain `--self-contained true -p:PublishAot=true`, and emit `artifacts/release/CrabDesk-Setup-x64.exe`.

- [ ] **Step 4: Verify the bootstrapper artifact**

Run: `./build/publish-bootstrapper.ps1 -Version 20260826.99`

Run: `./build/verify-bootstrapper.ps1`

Expected: a single Native AOT executable exists, no adjacent `coreclr.dll` is required, and diagnostic mode reports the local dependency state without downloading or installing anything.

### Task 3: Publish and package only the framework-dependent application payload

**Files:**
- Modify: `CrabDesk.WinUI/CrabDesk.WinUI.csproj`
- Modify: `build/publish.ps1`
- Modify: `build/publish-winui.ps1`
- Modify: `build/build-installer.ps1`
- Modify: `installer/CrabDesk.iss`
- Modify: `build/verify-installer.ps1`
- Modify: `build/verify-all.ps1`

- [ ] **Step 1: Remove the Full/Web publish split**

Set `WindowsAppSDKSelfContained` to `false`, remove `CrabDeskPackageKind`, and always publish `win-x64` with `--self-contained false -p:WindowsAppSDKSelfContained=false`.

- [ ] **Step 2: Rename the embedded app installer**

Remove Inno Setup package-kind branches and emit `artifacts/installer/CrabDesk-Payload-x64.exe` containing the framework-dependent application files while preserving per-user installation, shortcuts, update replacement, uninstall behavior, and data retention.

- [ ] **Step 3: Update lifecycle verification**

Point direct installer lifecycle checks at the payload and rename verification labels from self-contained/full/web terminology to framework-dependent payload terminology.

- [ ] **Step 4: Build and inspect the payload**

Run: `./build/publish.ps1 -Version 20260826.99`

Run: `./build/build-installer.ps1 -Version 20260826.99`

Expected: `CrabDesk-Payload-x64.exe` exists, the publish directory contains `CrabDesk.WinUI.runtimeconfig.json`, and it does not contain app-local `coreclr.dll`, `hostfxr.dll`, or `Microsoft.WindowsAppRuntime.dll`.

### Task 4: Unify application updates and GitHub Release assets

**Files:**
- Modify: `CrabDesk.Runtime/UpdateConfiguration.cs`
- Modify: `CrabDesk.Core/GitHubUpdateService.cs`
- Modify: `CrabDesk.Tests/UpdateServiceTests.cs`
- Modify: `.github/workflows/release.yml`
- Modify: `build/verify-release-workflow.ps1`
- Modify: `build/verify-github-release.ps1`
- Modify: `build/verify-signing.ps1`

- [ ] **Step 1: Add a failing fixed-asset update test**

Assert that every installed CrabDesk build selects only `CrabDesk-Setup-x64.exe`, with no package-kind-specific fallback or asset names.

- [ ] **Step 2: Simplify update selection**

Remove Full/Web asset normalization and assembly metadata branching; keep the fixed public installer name `CrabDesk-Setup-x64.exe` throughout update checks and downloads.

- [ ] **Step 3: Reorder the release pipeline**

Publish and sign the framework-dependent application, build and sign the payload, embed it into the Native AOT setup, sign the final setup, create its checksum, and upload only `CrabDesk-Setup-x64.exe` and `SHA256SUMS.txt`.

- [ ] **Step 4: Update release verification**

Require the public setup and checksum file; verify its hash and signature; run install lifecycle checks against the public setup; and assert the workflow neither references `CrabDesk-Setup-Web-x64.exe` nor uploads the embedded payload separately.

### Task 5: Update documentation and complete verification

**Files:**
- Modify: `README.md`
- Modify: `docs/DEVELOPMENT_PLAN.md`
- Modify: `docs/EXTERNAL_VALIDATION.md`
- Modify: `docs/releases/v1.0.0.md`

- [ ] **Step 1: Document the single public setup**

Explain that users download only `CrabDesk-Setup-x64.exe`; it checks and downloads missing Microsoft prerequisites; `CrabDesk-Payload-x64.exe` is an internal same-release payload; the setup itself remains Native AOT so it works before .NET is installed.

- [ ] **Step 2: Run source-policy checks**

Run: `rg -n "CrabDesk-Setup-Web-x64|CrabDeskPackageKind|PackageKind|win-x64-web|Self-contained publish|完整安装包" CrabDesk.* build installer .github README.md docs`

Expected: no active release/build/update references to the retired dual-installer design.

- [ ] **Step 3: Run automated verification**

Run: `dotnet test CrabDesk.sln -c Release`

Run: `./build/verify-release-workflow.ps1`

Run: `./build/publish.ps1 -Version 20260826.99`

Run: `./build/build-installer.ps1 -Version 20260826.99`

Run: `./build/publish-bootstrapper.ps1 -Version 20260826.99`

Run: `./build/verify-bootstrapper.ps1`

Expected: all tests and policy checks pass and all three release-stage artifacts are produced in their expected directories.

### Self-review

- Spec coverage: application publishing, prerequisite detection/download, one public setup, update selection, signing, checksums, workflow, and documentation are covered.
- Placeholder scan: no deferred implementation markers are present.
- Type consistency: the public asset is consistently `CrabDesk-Setup-x64.exe`; the internal payload is consistently `CrabDesk-Payload-x64.exe`; the application publish directory is consistently `artifacts/publish/win-x64`.
- Repository policy: no commits or pushes are included because the user did not request either operation in this turn.
