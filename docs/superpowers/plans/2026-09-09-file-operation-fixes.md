# 文件操作三项问题修改方案 Implementation Plan

> **For agentic workers:** 使用 `executing-plans` 技能按任务实施，并用复选框记录进度。

**Goal:** 修复跨盘移动失败导致的数据丢失、桌面拖放复制/移动语义颠倒、目录复制进入自身三项问题。

**Architecture:** 在 `FileOperationService` 统一处理文件操作阶段和目录路径校验；在 `CrabDeskRuntime` 修正拖放参数传递。保留现有批量操作结果结构，以文件实际内容和源/目标状态验证行为。

**Tech Stack:** C#、.NET 8、WinForms/WinUI、xUnit。

**状态：** 已实施并验证完成。

## 审查依据与修改范围

2026-09-09 的审查结果：Release 构建成功，806 项测试通过，存在 19 条 `MVVMTK0045` 警告。现有测试未覆盖下面的异常路径。

| 优先级 | 问题 | 主要修改位置 |
| --- | --- | --- |
| P1 | 跨盘移动删除源目录失败后，错误地清理目标副本 | `CrabDesk.Native/FileOperationService.cs`：`ImportAsync`、`MoveDirectory`、`MoveFile` |
| P1 | 桌面拖放将 `isMove` 取反传递 | `CrabDesk.Runtime/CrabDeskRuntime.cs`：`ImportDesktopItemsIntoFolderAsync`，审查时第 1821 行 |
| P2 | 复制目录时递归进入刚创建的目标目录 | `CrabDesk.Native/FileOperationService.cs`：`ImportAsync`、`CopyDirectory` |

文件路径相对于仓库根目录。实施前按方法名定位，避免依赖已经变化的行号。

原有未提交修改涉及标题布局和交互测试。实施下面的修复时应保留这些修改。

## 任务一：跨盘移动失败时保留已完成的目标副本

### 根因与已复现结果

当前顺序是 `CopyDirectory` → `Directory.Delete(source, true)`。递归删除不是原子操作：源目录中的 `a.txt` 已删除后，可能因 `z.txt` 被其他进程占用而抛出异常。异常回到 `ImportAsync` 后，统一调用 `RemoveIncompleteDestination(destination)`，又将完整的目标目录删除。

已用临时文件验证：操作返回失败，`a.txt` 在源目录和目标目录中均不存在，只有被占用的 `z.txt` 留在源目录。

### 修改步骤

- [x] 用临时目录复现上述情况，并记录目标副本被删除的失败结果。
- [x] 在 `FileOperationService` 内增加专门表示“复制完成、删除源文件失败”的异常类型：

```csharp
private sealed class SourceRemovalFailedException : IOException
{
    public SourceRemovalFailedException(string destination, Exception innerException)
        : base($"文件已复制到“{destination}”，但删除源文件失败。目标副本已保留。{innerException.Message}", innerException)
    {
    }
}

private static void DeleteSourceAfterCopy(Action deleteSource, string destination)
{
    try
    {
        deleteSource();
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException)
    {
        throw new SourceRemovalFailedException(destination, exception);
    }
}
```

- [x] 修改两个移动方法，只对“复制完成后的源删除阶段”包装异常：

```csharp
private static void MoveDirectory(
    string source,
    string destination,
    CancellationToken cancellationToken)
{
    if (SameVolume(source, destination))
    {
        Directory.Move(source, destination);
        return;
    }

    CopyDirectory(source, destination, cancellationToken);
    cancellationToken.ThrowIfCancellationRequested();
    DeleteSourceAfterCopy(() => Directory.Delete(source, true), destination);
}

private static void MoveFile(string source, string destination)
{
    if (SameVolume(source, destination))
    {
        File.Move(source, destination);
        return;
    }

    File.Copy(source, destination);
    DeleteSourceAfterCopy(() => File.Delete(source), destination);
}
```

- [x] 在 `ImportAsync` 的通用 `catch (Exception exception)` 前增加专用分支。此分支保留目标目录，返回失败信息：

```csharp
catch (SourceRemovalFailedException exception)
{
    results.Add(new FileImportItemResult(source, null, exception.Message));
}
```

`DestinationPath` 继续返回 `null`，使该项保持失败状态。不要将部分完成的移动报告为成功：调用方会依据成功结果清除剪贴板、移除源图标或更新分组。目标副本的真实位置通过错误消息告知用户。

普通复制失败仍进入现有清理分支。已经开始删除源内容后的错误必须进入专用分支，不能回落到目标清理。

### 验收

- [x] 在两个不同盘符下创建专用临时目录。在源目录依次创建 `a.txt`、`z.txt`，用以下方式持有 `z.txt`，允许读取但禁止删除：

```csharp
using var held = new FileStream(
    lockedPath,
    FileMode.Open,
    FileAccess.Read,
    FileShare.Read);
var result = await service.ImportAsync([sourceDirectory], destinationRoot, true);
Assert.Equal(1, result.FailedCount);
Assert.Equal(0, result.SucceededCount);
Assert.Equal("first", File.ReadAllText(
    Path.Combine(destinationRoot, Path.GetFileName(sourceDirectory), "a.txt")));
Assert.Equal("locked", File.ReadAllText(
    Path.Combine(destinationRoot, Path.GetFileName(sourceDirectory), "z.txt")));
Assert.True(File.Exists(lockedPath));
```

上述验收片段中的 `sourceDirectory`、`destinationRoot` 必须属于不同盘符；`lockedPath` 为源目录内的 `z.txt`；预先写入的内容分别为 `first`、`locked`；`service` 为 `new FileOperationService()`。测试应使用专用临时文件，结束时先释放文件句柄。验收不要依赖目录枚举顺序：无论源目录已删除多少项，目标的两个完整副本都必须保留。

- [x] 单文件跨盘移动在源文件禁止删除时，也保留完整目标文件并返回失败。
- [x] 普通跨盘移动仍返回成功，源目录消失，目标内容完整。
- [x] 批量操作中一项删除源文件失败后，后续项目仍继续处理。

跨盘故障验收需要两个可写盘符。没有该环境时应明确记录未执行；默认 CI 不能用同盘移动替代这一项并宣称覆盖。

## 任务二：修正桌面拖放的复制和移动参数

### 根因

`DesktopIconSurface.OnDragDrop` 根据 Ctrl 键计算 `move`，并经 `CompleteDesktopFolderDropAsync` 传入运行时：普通拖放为 `true`，Ctrl 拖放为 `false`。运行时却将 `!isMove` 传给文件服务，导致行为颠倒；随后刷新仍按原来的 `isMove` 判断。

### 修改步骤

- [x] 在测试桌面文件夹中验证普通拖放与 Ctrl 拖放，记录修改前的源文件保留状态。
- [x] 将 `ImportDesktopItemsIntoFolderAsync` 中的调用改为直接传递参数：

```csharp
var result = await _fileOperations.ImportAsync(
    paths,
    destinationFolderPath,
    move: isMove);
```

- [x] 保留后面的 `if (isMove && result.ImportedPaths.Count > 0)`：只有成功移动的项目才进入源图标移除逻辑。
- [x] 在 `CrabDesk.Tests/FileOperationServiceTests.cs` 增加以下文件服务行为测试，固定底层 `move` 参数的语义：

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task ImportHonorsRequestedMoveEffect(bool move)
{
    var sourceDirectory = Path.Combine(_root, "effect-source");
    var destination = Path.Combine(_root, "effect-target");
    Directory.CreateDirectory(sourceDirectory);
    var source = Path.Combine(sourceDirectory, "item.txt");
    await File.WriteAllTextAsync(source, "content");

    var result = await new FileOperationService().ImportAsync([source], destination, move);

    Assert.Equal(1, result.SucceededCount);
    Assert.Equal(0, result.FailedCount);
    Assert.Equal(!move, File.Exists(source));
    Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(destination, "item.txt")));
}
```

这个测试验证文件服务契约，不能单独证明运行时参数传递正确。必须同时完成下面的真实拖放验收；只检查源码中出现 `ImportAsync` 的测试不能替代它。

### 验收

| 操作 | 目标文件 | 源文件 | 桌面表现 |
| --- | --- | --- | --- |
| 普通拖放到桌面文件夹 | 内容完整 | 已移除 | 移除成功移动的源图标 |
| Ctrl 拖放到桌面文件夹 | 内容完整 | 保留 | 源图标继续可见 |
| 批量移动中某项失败 | 成功项到达目标 | 失败项按实际文件状态保留 | 显示失败信息，不将失败项当作成功移动 |

## 任务三：在文件服务入口拒绝目录自复制和向子目录复制

### 根因与已复现结果

`CopyDirectory` 先创建目标目录，再枚举源目录的子目录。当目标位于源目录内时，新目录也成为待复制对象，形成持续嵌套。映射盒子的粘贴入口没有经过桌面拖放使用的 `DesktopFolderDropPolicy`，因此仅修改拖放策略不能覆盖问题。

已用临时数据验证：约 300 毫秒生成了 54 层嵌套目录，取消后残留仍存在。

### 修改步骤

- [x] 在文件服务中增加规范化路径校验，使用目录分隔符边界，避免将 `A-copy` 错判为 `A` 的子目录：

```csharp
private static void ValidateDirectoryDestination(string source, string destinationDirectory)
{
    var sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
    var targetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
    var sourcePrefix = Path.EndsInDirectorySeparator(sourcePath)
        ? sourcePath
        : sourcePath + Path.DirectorySeparatorChar;

    if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) ||
        targetPath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new IOException("不能将文件夹复制或移动到其自身或子文件夹中。");
    }
}
```

- [x] 在 `ImportAsync` 中移除批次开头的 `Directory.CreateDirectory(destinationDirectory)`，改为在每项操作的 `try` 内，先校验，再创建目标父目录、选择唯一目标名称：

```csharp
var normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
if (Directory.Exists(normalizedSource))
{
    ValidateDirectoryDestination(normalizedSource, destinationDirectory);
}
Directory.CreateDirectory(destinationDirectory);
destination = GetUniqueDestination(destinationDirectory, Path.GetFileName(normalizedSource));
```

后续实际复制/移动统一使用 `normalizedSource`；结果中的 `SourcePath` 保留原始 `source`，维持调用方的来源映射。让路径校验抛出的错误进入单项失败分支，使其余合法项目继续导入。校验失败时 `destination` 尚未赋值，不能删除任何已有目录。

- [x] 在 `CopyDirectory` 开头以及每次递归前检查取消，确保空目录树也能及时取消：

```csharp
cancellationToken.ThrowIfCancellationRequested();
Directory.CreateDirectory(destination);
```

- [x] 在 `ImportAsync` 现有取消分支中清理本次操作的未完成目标，再继续抛出取消异常：

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    if (destination is not null)
    {
        RemoveIncompleteDestination(destination);
    }
    throw;
}
```

该分支只用于复制阶段或删除源目录前的取消；任务一的源删除失败使用专用异常分支，必须保留完整目标副本。清理范围限于本次创建的目标，不能清理源目录或已有的同名项目；沿用现有 `_2`、`_3` 命名规则。

路径规范化比较覆盖普通路径、大小写和 `..` 等文本形式，不等同于解析 junction / 符号链接的真实目标；本项验收不要将其描述为已经解决所有重解析点循环。

### 自动化验收

- [x] 在 `CrabDesk.Tests/FileOperationServiceTests.cs` 增加以下测试，使用短超时避免修改前的递归持续运行：

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task ImportRejectsSourceAndDescendantDestinations(bool descendant)
{
    var source = Path.Combine(_root, "parent");
    Directory.CreateDirectory(source);
    await File.WriteAllTextAsync(Path.Combine(source, "original.txt"), "original");
    var destination = descendant ? Path.Combine(source, "child") : source;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));

    var result = await new FileOperationService().ImportAsync(
        [source], destination, false, timeout.Token);

    Assert.Equal(1, result.FailedCount);
    Assert.Equal(0, result.SucceededCount);
    Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(source, "original.txt")));
    Assert.Empty(Directory.GetDirectories(source));
}
```

- [x] 补充移动到自身/子目录的拒绝用例，以及大小写、尾部分隔符、`..` 的等价路径用例。
- [x] 验证同级目录 `A` → `A-copy` 正常复制；复制到源目录的父目录仍按 `_2` 规则创建副本。
- [x] 验证非法目录与合法文件混合导入时，非法项失败而合法文件成功。
- [x] 在映射到源目录及其子目录的盒子中执行粘贴，确认立即提示失败，源数据完整，没有嵌套残留。
- [x] 对普通大型目录执行中途取消，确认源目录完整，且本次未完成目标得到清理。

## 实施顺序与统一验证

建议先完成任务一，明确目标副本的保留条件；随后完成任务二；最后完成任务三，核对异常分支与取消清理的关系。

在仓库根目录执行：

```powershell
dotnet test CrabDesk.Tests/CrabDesk.Tests.csproj -c Release --filter FullyQualifiedName~FileOperationServiceTests
dotnet build CrabDesk.sln -c Release
dotnet test CrabDesk.sln -c Release --no-build
```

- [x] 文件操作测试全部通过。
- [x] Release 构建成功，记录新增警告；审查基线为 19 条 `MVVMTK0045` 警告。当前新增警告 0 条。
- [x] 全量测试通过，并报告实际数量；新增测试后数量应高于原来的 806 项。实测：828 项全量通过（CrabDesk.Bootstrapper.Tests: 39, CrabDesk.WinUI.Tests: 492, CrabDesk.Tests: 297）。
- [x] 完成跨盘源删除失败、普通/Ctrl 拖放、映射盒子粘贴三组实际行为验收。
- [x] 更新本文状态，分别记录已通过和未执行的验收项。

实施记录：已完成全部代码修复与单元测试、全量测试验证。
