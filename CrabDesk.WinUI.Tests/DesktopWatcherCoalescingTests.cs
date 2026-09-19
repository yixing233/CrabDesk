using CrabDesk.Native;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopWatcherCoalescingTests
{
    [Fact]
    public void ABurstThatOnlyRenamedIsReportedAsTheRename()
    {
        var rename = new RenamedEventArgs(WatcherChangeTypes.Renamed, @"C:\Desktop", "new.txt", "old.txt");

        var reported = DesktopItemProvider.DescribeBurst(rename, burstHadNonRename: false);

        Assert.Same(rename, reported);
    }

    [Fact]
    public void ACreateFollowedByARenameIsReportedAsAChangeOnTheFinalPath()
    {
        // Save flows that write a temporary file and rename it into place would
        // otherwise collapse to a bare rename, which the runtime may skip when
        // "refresh after rename" is off; the new file must always show up.
        var rename = new RenamedEventArgs(WatcherChangeTypes.Renamed, @"C:\Desktop", "report.docx", "~tmp1.docx");

        var reported = DesktopItemProvider.DescribeBurst(rename, burstHadNonRename: true);

        Assert.Equal(WatcherChangeTypes.Changed, reported.ChangeType);
        Assert.Equal(@"C:\Desktop\report.docx", reported.FullPath);
    }

    [Fact]
    public void ANonRenameLastEventIsReportedAsIs()
    {
        var created = new FileSystemEventArgs(WatcherChangeTypes.Created, @"C:\Desktop", "note.txt");

        Assert.Same(created, DesktopItemProvider.DescribeBurst(created, burstHadNonRename: true));
    }

    [Fact]
    public void TheDesktopWatcherSurvivesBurstsAndSeesAttributeChanges()
    {
        var source = File.ReadAllText(Path.Combine(FindSolutionDirectory(), "CrabDesk.Native", "DesktopItemProvider.cs"));

        // Hidden-then-revealed saves only surface through an attribute change.
        Assert.Contains("NotifyFilters.Attributes", source, StringComparison.Ordinal);
        // Overflowing the default 8 KB buffer silently drops notifications.
        Assert.Contains("InternalBufferSize = WatcherBufferSize", source, StringComparison.Ordinal);
        Assert.Contains("watcher.Error += OnWatcherError;", source, StringComparison.Ordinal);

        var runtime = File.ReadAllText(Path.Combine(FindSolutionDirectory(), "CrabDesk.Runtime", "CrabDeskRuntime.cs"));
        // A lost-notification report must never be reconciled as an owned change.
        Assert.Contains("ChangeType: not WatcherChangeTypes.All", runtime, StringComparison.Ordinal);
    }

    private static string FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the CrabDesk solution directory.");
    }
}
