using System.Windows.Forms;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopIconInteractionTests
{
    [Fact]
    public void ExternalFileDragOffersMoveForRecycleBinAndCopyForFileTargets()
    {
        var effects = DesktopIconSurface.ExternalFileDropEffects;

        Assert.True(effects.HasFlag(DragDropEffects.Copy));
        Assert.True(effects.HasFlag(DragDropEffects.Move));
    }
}
