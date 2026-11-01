using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// Serializes live desktop probes and native window ordering tests. Explorer
/// reactions, foreground focus and the global Z-order must not be changed by
/// another test while a probe or window-order assertion is running.
/// </summary>
[CollectionDefinition("desktop-probe", DisableParallelization = true)]
public sealed class DesktopProbeCollection
{
}
