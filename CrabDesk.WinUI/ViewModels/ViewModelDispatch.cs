using Microsoft.UI.Dispatching;

namespace CrabDesk.WinUI.ViewModels;

/// <summary>
/// Runtime events can be raised from thread-pool threads (AI operations,
/// takeover retries); handlers that mutate observable collections must run
/// on the UI thread that created the collections.
/// </summary>
internal static class ViewModelDispatch
{
    public static DispatcherQueue? CaptureDispatcherQueue()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static void Run(DispatcherQueue? queue, Action action)
    {
        if (queue is { } dispatcherQueue && !dispatcherQueue.HasThreadAccess)
        {
            dispatcherQueue.TryEnqueue(() => action());
            return;
        }

        action();
    }
}
