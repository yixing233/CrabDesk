using CrabDesk.Core;

namespace CrabDesk.Native;

public interface IControlConnectionService : IDisposable
{
    ControlConnectionKind Kind { get; }
    Task<ControlConnectionSnapshot> ReadAsync(bool scan, CancellationToken token = default);
    Task<ControlOperationResult> SetEnabledAsync(bool enabled, CancellationToken token = default);
    Task<ControlOperationResult> ConnectAsync(string id, string? password, CancellationToken token = default);
    Task<ControlOperationResult> DisconnectAsync(string id, CancellationToken token = default);
}
