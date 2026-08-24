using CrabDesk.Core;

namespace CrabDesk.WinUI.Services;

public sealed record AiOrganizationDialogRequest(
    Func<IProgress<AiClassificationProgress>, IProgress<string>, CancellationToken, Task<AiClassificationPreview>> PreviewAsync,
    Func<AiClassificationPreview, CancellationToken, Task<AiClassificationApplyResult>> ApplyAsync,
    Action Cancel);

public enum AiOrganizationDialogOutcome
{
    Applied,
    Cancelled,
    NoWork,
    NoAssignments,
    Failed
}

public sealed record AiOrganizationDialogResult(
    AiOrganizationDialogOutcome Outcome,
    string Message,
    AiClassificationApplyResult? ApplyResult = null);
