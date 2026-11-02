using System.Runtime.InteropServices;
using System.Text;

namespace CrabDesk.Native;

/// <summary>
/// Reads the SMBIOS memory-module table that Windows caches for the firmware.
///
/// WMI's Win32_PhysicalMemory is the usual source, but it needs a package
/// reference and takes a few hundred milliseconds, and many laptops leave the
/// part number blank anyway. The raw table gives the memory type and speed that
/// those machines do populate, at a fraction of the cost.
/// </summary>
public static class WidgetSmbios
{
    /// <summary>One installed memory module.</summary>
    public sealed record Module(string PartNumber, string Manufacturer, string TypeName, int SpeedMhz, long SizeBytes);

    private const uint RawSmbiosProvider = 0x52534D42; // 'RSMB'
    private const byte MemoryDeviceType = 17;
    private const byte EndOfTableType = 127;

    /// <summary>
    /// Reads the firmware table and returns its memory modules. Returns an empty
    /// list when the firmware exposes no table; the caller treats that as
    /// "unknown" rather than as an error.
    /// </summary>
    public static IReadOnlyList<Module> Read()
    {
        var size = GetSystemFirmwareTable(RawSmbiosProvider, 0, null, 0);
        if (size <= 8) return [];
        var buffer = new byte[size];
        if (GetSystemFirmwareTable(RawSmbiosProvider, 0, buffer, size) != size) return [];
        // The raw table is prefixed with 8 bytes of provider header before the
        // SMBIOS structures begin.
        return Parse(buffer.AsSpan(8));
    }

    /// <summary>
    /// Walks the SMBIOS structure table and collects Type 17 records. Pure over
    /// its input so the layout handling can be tested without firmware access.
    /// </summary>
    public static IReadOnlyList<Module> Parse(ReadOnlySpan<byte> table)
    {
        var modules = new List<Module>();
        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            var type = table[offset];
            if (type == EndOfTableType) break;
            var length = table[offset + 1];
            if (length < 4 || offset + length > table.Length) break;
            if (type == MemoryDeviceType && length >= 0x15)
            {
                // Pass the rest of the table, not just the formatted area: the
                // string set follows it and holds the part number.
                if (ReadModule(table[offset..]) is { } module) modules.Add(module);
            }
            offset = NextStructure(table, offset, length);
            if (offset < 0) break;
        }
        return modules;
    }

    private static Module? ReadModule(ReadOnlySpan<byte> record)
    {
        // SMBIOS 2.1+ Type 17 layout. Offsets are fixed; the strings live after
        // the formatted area and are addressed by their 1-based index.
        // Size is in megabytes unless it is the 0x7FFF escape, which points at
        // the extended 32-bit field that is measured in megabytes as well.
        var size = ReadWord(record, 0x0C);
        if (size == 0) return null;
        long megabytes = size == 0x7FFF ? ReadDword(record, 0x1C) : size;
        long bytes = megabytes * 1024 * 1024;
        if (bytes <= 0) return null;

        var speed = (int)ReadWord(record, 0x15);
        if (speed == 0xFFFF && record.Length >= 0x58) speed = (int)ReadDword(record, 0x54);
        var typeCode = record[0x12];
        var strings = ReadStrings(record);
        return new Module(At(strings, record[0x18]), At(strings, record[0x17]), MemoryTypeName(typeCode), speed, bytes);
    }

    /// <summary>Offset of the next structure, skipping the string set and its double terminator.</summary>
    private static int NextStructure(ReadOnlySpan<byte> table, int offset, int length)
    {
        var cursor = offset + length;
        while (cursor + 1 < table.Length && !(table[cursor] == 0 && table[cursor + 1] == 0))
        {
            cursor++;
        }
        cursor += 2;
        return cursor <= table.Length ? cursor : -1;
    }

    private static List<string> ReadStrings(ReadOnlySpan<byte> record)
    {
        var strings = new List<string>();
        var length = (int)record[1];
        var cursor = length;
        while (cursor < record.Length && record[cursor] != 0)
        {
            var end = cursor;
            while (end < record.Length && record[end] != 0) end++;
            strings.Add(Encoding.ASCII.GetString(record[cursor..end]).Trim());
            cursor = end + 1;
        }
        return strings;
    }

    private static string At(List<string> strings, int index) =>
        index >= 1 && index <= strings.Count ? strings[index - 1] : "";

    private static int ReadWord(ReadOnlySpan<byte> record, int offset) =>
        offset + 1 < record.Length ? record[offset] | record[offset + 1] << 8 : 0;

    private static long ReadDword(ReadOnlySpan<byte> record, int offset) =>
        offset + 3 < record.Length
            ? record[offset] | record[offset + 1] << 8 | record[offset + 2] << 16 | (long)record[offset + 3] << 24
            : 0;

    /// <summary>
    /// SMBIOS 3.x memory type codes. Reserved and "other/unknown" codes return
    /// an empty string so the card omits the type instead of printing a number.
    /// </summary>
    public static string MemoryTypeName(byte code) => code switch
    {
        0x03 => "DRAM", 0x04 => "EDRAM", 0x05 => "VRAM", 0x06 => "SRAM", 0x07 => "RAM",
        0x08 => "ROM", 0x09 => "Flash", 0x0A => "EEPROM", 0x0B => "FEPROM", 0x0C => "EPROM",
        0x0D => "CDRAM", 0x0E => "3DRAM", 0x0F => "SDRAM", 0x10 => "SGRAM", 0x11 => "RDRAM",
        0x12 => "DDR", 0x13 => "DDR2", 0x14 => "DDR2 FB-DIMM",
        0x18 => "DDR3", 0x19 => "FBD2", 0x1A => "DDR4",
        0x1B => "LPDDR", 0x1C => "LPDDR2", 0x1D => "LPDDR3", 0x1E => "LPDDR4",
        0x1F => "NVDIMM", 0x20 => "HBM", 0x21 => "HBM2", 0x22 => "DDR5", 0x23 => "LPDDR5",
        0x24 => "HBM3",
        _ => ""
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);
}
