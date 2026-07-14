using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Microsoft.VisualBasic.FileIO;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using WpfDataObject = System.Windows.IDataObject;

namespace CrabDesk.Native;

public sealed class FileOperationService : IFileOperationService
{
    public void Open(DesktopItemRef item)
    {
        Process.Start(new ProcessStartInfo(item.ParsingName) { UseShellExecute = true });
    }

    public void OpenLocation(DesktopItemRef item)
    {
        if (item.FileSystemPath is null)
        {
            Open(item);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FileSystemPath}\"")
        {
            UseShellExecute = true
        });
    }

    public void ShowProperties(DesktopItemRef item)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Verb = "properties",
            File = item.ParsingName,
            Show = 1,
            Mask = 0x0000000C
        };
        ShellExecuteEx(ref info);
    }

    public Task<string> RenameAsync(DesktopItemRef item, string newName, CancellationToken cancellationToken = default)
    {
        if (item.FileSystemPath is null)
        {
            throw new InvalidOperationException("系统项目不能重命名。");
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            newName = newName.Trim();
            if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("名称为空或包含无效字符。", nameof(newName));
            }
            var directory = Path.GetDirectoryName(item.FileSystemPath)!;
            var extension = item.Kind is DesktopItemKind.File or DesktopItemKind.Shortcut
                ? Path.GetExtension(item.FileSystemPath)
                : string.Empty;
            if (!string.IsNullOrEmpty(extension) && newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                newName = newName[..^extension.Length];
            }
            var destination = Path.Combine(directory, newName + extension);
            if (string.Equals(item.FileSystemPath, destination, StringComparison.OrdinalIgnoreCase))
            {
                return destination;
            }
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new IOException($"“{Path.GetFileName(destination)}”已经存在。");
            }
            if (item.Kind == DesktopItemKind.Folder)
            {
                Directory.Move(item.FileSystemPath, destination);
            }
            else
            {
                File.Move(item.FileSystemPath, destination);
            }
            return destination;
        }, cancellationToken);
    }

    public Task DeleteAsync(IEnumerable<DesktopItemRef> items, CancellationToken cancellationToken = default)
    {
        var paths = items.Where(item => item.FileSystemPath is not null).Select(item => item.FileSystemPath!).ToArray();
        return Task.Run(() =>
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(path))
                {
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                else if (File.Exists(path))
                {
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ImportAsync(
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        bool move,
        CancellationToken cancellationToken = default)
    {
        var sources = sourcePaths.ToArray();
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            Directory.CreateDirectory(destinationDirectory);
            var imported = new List<string>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = GetUniqueDestination(destinationDirectory, Path.GetFileName(source));
                if (Directory.Exists(source))
                {
                    if (move)
                    {
                        MoveDirectory(source, destination, cancellationToken);
                    }
                    else
                    {
                        CopyDirectory(source, destination, cancellationToken);
                    }
                }
                else if (move)
                {
                    MoveFile(source, destination);
                }
                else
                {
                    File.Copy(source, destination);
                }
                imported.Add(destination);
            }
            return imported;
        }, cancellationToken);
    }

    public void SetClipboardFiles(IEnumerable<DesktopItemRef> items, bool move)
    {
        var paths = items
            .Select(item => item.FileSystemPath)
            .OfType<string>()
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }
        Clipboard.SetDataObject(FileClipboardCodec.Create(paths, move), true);
    }

    public FileClipboardContent GetClipboardFiles() =>
        FileClipboardCodec.Read(Clipboard.GetDataObject());

    public void ClearClipboardFiles()
    {
        var content = GetClipboardFiles();
        if (content.HasFiles)
        {
            Clipboard.Clear();
        }
    }

    private static string GetUniqueDestination(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var child in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)), cancellationToken);
        }
    }

    private static void MoveDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        if (SameVolume(source, destination))
        {
            Directory.Move(source, destination);
            return;
        }
        CopyDirectory(source, destination, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Delete(source, true);
    }

    private static void MoveFile(string source, string destination)
    {
        if (SameVolume(source, destination))
        {
            File.Move(source, destination);
            return;
        }
        File.Copy(source, destination);
        File.Delete(source);
    }

    private static bool SameVolume(string first, string second) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(first)),
            Path.GetPathRoot(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo executeInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        internal int Size;
        internal uint Mask;
        internal IntPtr Hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Verb;
        [MarshalAs(UnmanagedType.LPWStr)] internal string File;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Directory;
        internal int Show;
        internal IntPtr Instance;
        internal IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Class;
        internal IntPtr ClassKey;
        internal uint HotKey;
        internal IntPtr IconOrMonitor;
        internal IntPtr Process;
    }
}

public static class FileClipboardCodec
{
    private const string PreferredDropEffect = "Preferred DropEffect";
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;

    public static DataObject Create(IEnumerable<string> paths, bool move)
    {
        var normalized = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, normalized);
        var effect = BitConverter.GetBytes(move ? DropEffectMove : DropEffectCopy);
        data.SetData(PreferredDropEffect, new MemoryStream(effect, writable: false));
        return data;
    }

    public static FileClipboardContent Read(WpfDataObject? data)
    {
        if (data?.GetDataPresent(DataFormats.FileDrop) != true ||
            data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return new FileClipboardContent([], false);
        }
        var move = false;
        if (data.GetDataPresent(PreferredDropEffect) && data.GetData(PreferredDropEffect) is Stream stream)
        {
            try
            {
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }
                Span<byte> buffer = stackalloc byte[4];
                if (stream.Read(buffer) == buffer.Length)
                {
                    move = (BitConverter.ToInt32(buffer) & DropEffectMove) != 0;
                }
            }
            catch (IOException)
            {
            }
        }
        return new FileClipboardContent(
            paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            move);
    }
}
