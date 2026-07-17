using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CrabDesk.Native;

public sealed record AuthenticodeVerificationResult(
    bool IsTrusted,
    string SignerSubject = "",
    string Thumbprint = "",
    string Message = "");

public static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static AuthenticodeVerificationResult Verify(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return new AuthenticodeVerificationResult(false, Message: "安装包不存在");
        }

        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = fullPath
        };
        var fileInfoPointer = IntPtr.Zero;
        var trustDataPointer = IntPtr.Zero;
        try
        {
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // Revocation is requested through ProviderFlags below.
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfoPointer = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00000080 // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            trustDataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, false);
            var action = GenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer);
            if (status != 0)
            {
                return new AuthenticodeVerificationResult(false, Message: FormatTrustError(status));
            }

#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
#pragma warning restore SYSLIB0057
            return new AuthenticodeVerificationResult(
                true,
                certificate.Subject,
                certificate.Thumbprint,
                "Authenticode 签名可信");
        }
        catch (CryptographicException exception)
        {
            return new AuthenticodeVerificationResult(false, Message: $"读取签名失败：{exception.Message}");
        }
        catch (Win32Exception exception)
        {
            return new AuthenticodeVerificationResult(false, Message: $"验证签名失败：{exception.Message}");
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustData>(trustDataPointer);
                Marshal.FreeCoTaskMem(trustDataPointer);
            }
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }
        }
    }

    private static string FormatTrustError(uint status) => status switch
    {
        0x800B0100 => "安装包没有 Authenticode 签名",
        0x80096010 => "安装包签名摘要不匹配",
        0x800B0109 => "安装包签名证书不受信任",
        0x800B010C => "安装包签名证书已被吊销",
        0x800B0111 => "安装包签名被系统明确拒绝",
        0x80092013 => "签名吊销状态暂时不可用",
        _ => $"Authenticode 验证失败（0x{status:X8}）"
    };

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPointer;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }
}
