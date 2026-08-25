using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using CrabDesk.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace CrabDesk.WinUI.Services;

/// <summary>
/// Converts locally resolved Shell icons into WinUI image sources. The shell
/// lookup and PNG encoding run off the UI thread. WinUI image construction is
/// then explicitly marshalled back to the application's dispatcher, rather
/// than relying on an ambient synchronization context during page creation.
/// </summary>
public sealed class DesktopItemIconSourceFactory
{
    private const int IconPixelSize = 48;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ShellIconProvider _iconProvider;

    public DesktopItemIconSourceFactory(
        DispatcherQueue dispatcherQueue,
        ShellIconProvider iconProvider)
    {
        _dispatcherQueue = dispatcherQueue;
        _iconProvider = iconProvider;
    }

    public async Task<ImageSource?> LoadAsync(string parsingName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return null;
        }

        var png = await Task.Run(
                () => LoadPng(parsingName, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        if (png is null || png.Length == 0)
        {
            return null;
        }

        return await CreateImageSourceAsync(png, cancellationToken).ConfigureAwait(false);
    }

    private Task<ImageSource?> CreateImageSourceAsync(byte[] png, CancellationToken cancellationToken)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            return CreateImageSourceCoreAsync(png, cancellationToken);
        }

        var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() => _ = CompleteImageSourceAsync(png, cancellationToken, completion)))
        {
            completion.TrySetException(new InvalidOperationException("WinUI dispatcher queue is unavailable."));
        }

        return completion.Task;
    }

    private static async Task CompleteImageSourceAsync(
        byte[] png,
        CancellationToken cancellationToken,
        TaskCompletionSource<ImageSource?> completion)
    {
        try
        {
            completion.TrySetResult(await CreateImageSourceCoreAsync(png, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task<ImageSource?> CreateImageSourceCoreAsync(byte[] png, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        var output = stream.AsStreamForWrite();
        await output.WriteAsync(png, cancellationToken).ConfigureAwait(true);
        await output.FlushAsync(cancellationToken).ConfigureAwait(true);
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    private byte[]? LoadPng(string parsingName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var icon = _iconProvider.GetIcon(parsingName, IconPixelSize) ??
                ShellIconProvider.GetGenericFileIcon();
            if (icon is null)
            {
                return null;
            }

            // The runtime cache owns the source bitmap and can evict it while
            // other desktop components are resolving icons. Encode a private
            // copy so the WinUI workbench never reads a disposed cache entry.
            using var copy = new Bitmap(icon);
            using var output = new MemoryStream();
            copy.Save(output, ImageFormat.Png);
            cancellationToken.ThrowIfCancellationRequested();
            return output.ToArray();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Shell extensions can fail while Explorer is rebuilding. A
            // missing workbench icon must never interrupt classification.
            return null;
        }
    }
}
