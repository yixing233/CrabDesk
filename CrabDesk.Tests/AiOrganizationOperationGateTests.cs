using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class AiOrganizationOperationGateTests
{
    [Fact]
    public async Task RejectsASecondOperationWhileTheFirstIsRunning()
    {
        using var gate = new AiOrganizationOperationGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = gate.ExecuteAsync(async cancellationToken =>
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return 1;
        });
        await started.Task;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.ExecuteAsync(_ => Task.FromResult(2)));

        Assert.Equal("AI 整理正在运行，请等待当前操作完成。", error.Message);
        release.SetResult();
        Assert.Equal(1, await first);
        Assert.False(gate.IsRunning);
    }

    [Fact]
    public async Task CancelSignalsTheActiveOperationAndReleasesTheGate()
    {
        using var gate = new AiOrganizationOperationGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = gate.ExecuteAsync(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        });
        await started.Task;

        gate.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(gate.IsRunning);
        Assert.Equal(2, await gate.ExecuteAsync(_ => Task.FromResult(2)));
    }
}
