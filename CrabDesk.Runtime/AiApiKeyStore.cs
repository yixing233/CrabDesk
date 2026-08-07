using System.Runtime.InteropServices;
using System.Text;

namespace CrabDesk.Runtime;

internal static class AiApiKeyStore
{
    private const int CryptProtectUiForbidden = 0x1;

    internal static string Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }
            var protectedBytes = File.ReadAllBytes(path);
            var clearBytes = Transform(protectedBytes, protect: false);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static void Save(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (string.IsNullOrEmpty(value))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }
        var protectedBytes = Transform(Encoding.UTF8.GetBytes(value), protect: true);
        File.WriteAllBytes(path, protectedBytes);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            var inputBlob = new DataBlob { Size = input.Length, Data = inputPointer };
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out outputBlob);
            if (!succeeded)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                var output = new byte[outputBlob.Size];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int Size;
        internal IntPtr Data;
    }
}
