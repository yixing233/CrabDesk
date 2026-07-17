using CrabDesk.Native;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace CrabDesk.Tests;

public sealed class ShellContextMenuSessionTests
{
    [Fact]
    public async Task CreatesNativeShellMenuForItemsInSameFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CrabDeskShellMenu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new[]
        {
            Path.Combine(root, "first.txt"),
            Path.Combine(root, "second.txt")
        };
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(path, "native shell menu test");
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var session = ShellContextMenuSession.TryCreate(paths, IntPtr.Zero);
                completion.SetResult(session is not null);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NativeShellMenuCanOpenAndCloseWithoutDisposingItsOwner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CrabDeskShellMenu-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "native shell menu lifetime test");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Forms.Form
                {
                    ShowInTaskbar = false,
                    StartPosition = Forms.FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-2000, -2000),
                    Size = new System.Drawing.Size(80, 80)
                };
                form.Show();
                using var session = ShellContextMenuSession.TryCreate([path], form.Handle);
                if (session is null)
                {
                    completion.SetResult(false);
                    return;
                }
                using var timer = new Forms.Timer { Interval = 150 };
                var closed = false;
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    closed = EndMenu();
                };
                timer.Start();
                session.Show(form.Handle, -1950, -1950);
                completion.SetResult(closed && !form.IsDisposed);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndMenu();
}
