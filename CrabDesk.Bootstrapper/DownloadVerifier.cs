using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;

namespace CrabDesk.Bootstrapper;

internal sealed record DownloadProgressReport(
    long BytesDownloaded,
    long? TotalBytes,
    double SpeedBytesPerSecond,
    double ProgressPercentage);

internal static class DownloadVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal static async Task DownloadAsync(
        HttpClient client,
        Uri uri,
        string destination,
        long maximumBytes,
        IProgress<DownloadProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("下载地址必须使用 HTTPS。");
        }

        var partialPath = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        Exception? lastError = null;
        try
        {
            for (var attempt = 1; attempt <= SetupPolicy.DownloadMaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    InstallerLogger.Log("INFO", "download_attempt", new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt,
                        ["file"] = Path.GetFileName(destination),
                        ["maxBytes"] = maximumBytes
                    });
                    await DownloadAttemptAsync(client, uri, partialPath, maximumBytes, progress, cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(partialPath, destination, true);
                    return;
                }
                catch (Exception exception) when (IsRetryable(exception, cancellationToken) && attempt < SetupPolicy.DownloadMaxAttempts)
                {
                    lastError = exception;
                    InstallerLogger.LogException("download_retry", exception, new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt,
                        ["file"] = Path.GetFileName(destination)
                    });
                    TryDelete(partialPath);
                    await Task.Delay(SetupPolicy.GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    InstallerLogger.LogException("download_failed", exception, new Dictionary<string, object?>
                    {
                        ["file"] = Path.GetFileName(destination)
                    });
                    throw;
                }
            }
        }
        finally
        {
            if (lastError is not null)
            {
                TryDelete(partialPath);
            }
        }

        throw lastError ?? new InvalidOperationException("下载失败。");
    }

    internal static bool IsRetryable(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is HttpRequestException http)
        {
            return http.StatusCode is null || SetupPolicy.IsTransientDownloadStatus(http.StatusCode);
        }

        return exception is IOException or SocketException;
    }

    private static async Task DownloadAttemptAsync(
        HttpClient client,
        Uri uri,
        string destination,
        long maximumBytes,
        IProgress<DownloadProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { Scheme: "https" })
        {
            throw new InvalidDataException("下载重定向到了非 HTTPS 地址。");
        }
        var totalBytes = response.Content.Headers.ContentLength;
        if (totalBytes is > 0 and var contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException("下载文件超过允许的大小。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long written = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var speedEstimator = new DownloadSpeedEstimator();
        var lastReportTime = stopwatch.ElapsedMilliseconds;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            written += read;
            if (written > maximumBytes) throw new InvalidDataException("下载文件超过允许的大小。");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            if (elapsedMs - lastReportTime >= 150)
            {
                var speed = speedEstimator.AddSample(written, stopwatch.Elapsed);
                lastReportTime = elapsedMs;
                var percent = totalBytes is > 0 ? Math.Clamp((double)written / totalBytes.Value * 100.0, 0, 100) : 0;
                progress?.Report(new DownloadProgressReport(written, totalBytes, speed, percent));
            }
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new DownloadProgressReport(written, totalBytes, speedEstimator.CurrentSpeedBytesPerSecond, 100));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    internal static void VerifySha256(string path, string expectedHash)
    {
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("安装组件缺少有效的 SHA-256 校验值。");
        }

        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        var expected = Convert.FromHexString(expectedHash);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} 的 SHA-256 校验失败。");
        }
    }

    internal static void VerifyTrustedMicrosoftSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData(fileInfoPointer);
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            if (result != 0)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} 的 Authenticode 签名不受信任（0x{result:X8}）。");
            }

#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!certificate.Subject.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} 不是由 Microsoft Corporation 签名的组件。");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal WinTrustFileInfo(string path)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }

        private uint StructureSize;
        [MarshalAs(UnmanagedType.LPWStr)] private string FilePath;
        private IntPtr FileHandle;
        private IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal WinTrustData(IntPtr fileInfo)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UIChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00000080;
            UIContext = 0;
            SignatureSettings = IntPtr.Zero;
        }

        private uint StructureSize;
        private IntPtr PolicyCallbackData;
        private IntPtr SipClientData;
        private uint UIChoice;
        private uint RevocationChecks;
        private uint UnionChoice;
        private IntPtr FileInfo;
        private uint StateAction;
        private IntPtr StateData;
        private IntPtr UrlReference;
        private uint ProviderFlags;
        private uint UIContext;
        private IntPtr SignatureSettings;
    }
}
