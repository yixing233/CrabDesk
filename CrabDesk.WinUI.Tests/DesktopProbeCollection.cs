using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// Serializes the tests that write probe files to the real desktop and
/// Documents folders. They each measure how Explorer reacts to a new file, so
/// running two at once would have them react to each other's probe.
/// </summary>
[CollectionDefinition("desktop-probe", DisableParallelization = true)]
public sealed class DesktopProbeCollection
{
}
