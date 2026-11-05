using System.Text;
using Microsoft.Win32;

namespace CrabDesk.Native;

internal static class MonitorModelName
{
    public static string Read(string displayName)
    {
        try
        {
            if (!ActiveDisplayPaths.Read().TryGetValue(displayName, out var target)) return "";
            var parts = target.DevicePath.Replace(@"\\?\", "").Split('#');
            if (parts.Length < 3 || !parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)) return "";
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + string.Join("\\", parts.Take(3)) + @"\Device Parameters");
            var model = key?.GetValue("EDID") is byte[] edid ? ParseEdid(edid) : "";
            return string.IsNullOrEmpty(model) ? (string.IsNullOrEmpty(target.FriendlyName) ? parts[1] : target.FriendlyName) : model;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return ""; }
    }

    internal static string ParseEdid(byte[] edid)
    {
        if (edid.Length < 128 || edid[0] != 0 || edid[1] != 255 || edid[7] != 0) return "";
        for (var offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0 || edid[offset + 3] != 0xFC) continue;
            var model = Encoding.ASCII.GetString(edid, offset + 5, 13).Split('\n', '\r', '\0')[0].Trim();
            if (model.Length == 0) continue;
            var code = (edid[8] << 8) | edid[9];
            var letters = new[] { (code >> 10) & 31, (code >> 5) & 31, code & 31 };
            var manufacturer = letters.All(letter => letter is >= 1 and <= 26)
                ? new string(letters.Select(letter => (char)('A' + letter - 1)).ToArray()) : "";
            if (manufacturer == "XMI") manufacturer = "小米/REDMI";
            return manufacturer.Length == 0 || model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase)
                ? model : manufacturer + " " + model;
        }
        return "";
    }

}
