using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using CrabDesk.Core;

namespace CrabDesk.Native;

/// <summary>
/// Reads the model names of the CPU, GPU, memory and system disk once.
///
/// Everything comes from the registry and the drive list rather than WMI: a
/// Win32_DiskDrive query takes around 200 ms on a cold service host, which is
/// far too long to spend inside a one-second sampling loop, while the registry
/// keys answer in single-digit milliseconds. Names cannot change while the
/// machine runs, so the result is cached for the lifetime of the process.
/// </summary>
public sealed class WidgetHardwareInventory
{
    private WidgetHardwareNames? _names;

    /// <summary>Cached names; empty fields mean "not determined".</summary>
    public WidgetHardwareNames Read()
    {
        if (_names is { } cached) return cached;
        var names = new WidgetHardwareNames(
            SafeRead(ReadCpu), SafeRead(ReadGpu), SafeRead(ReadMemory), SafeRead(ReadDisk));
        _names = names;
        return names;
    }

    private static string SafeRead(Func<string> read)
    {
        try { return read() ?? ""; }
        // Hardware enumeration is best-effort decoration: a missing key or a
        // denied read must never break the metrics that do work.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or FormatException or OverflowException)
        {
            return "";
        }
    }

    private static string ReadCpu()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return Normalize(key?.GetValue("ProcessorNameString") as string);
    }

    // Discrete adapters are what a user recognises, but the registry lists
    // virtual display drivers (remote-desktop, USB monitors) alongside them.
    // Preferring the longest name with no virtual-adapter keyword keeps the
    // integrated GPU when it is the only real one, and the discrete card when
    // a laptop exposes both.
    private static readonly string[] VirtualAdapterMarkers =
    [
        "virtual", "remote", "idd", "indirect", "basic render", "mirror", "oray", "gameviewer"
    ];

    private static string ReadGpu()
    {
        using var classKey = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (classKey is null) return "";
        var candidates = new List<string>();
        foreach (var name in classKey.GetSubKeyNames())
        {
            // Only the four-digit adapter keys carry DriverDesc; the class key
            // also holds properties and other non-adapter subkeys.
            if (name.Length != 4 || !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            using var adapter = classKey.OpenSubKey(name);
            if (Normalize(adapter?.GetValue("DriverDesc") as string) is { Length: > 0 } description)
            {
                candidates.Add(description);
            }
        }
        if (candidates.Count == 0) return "";
        var real = candidates.Where(candidate =>
            !VirtualAdapterMarkers.Any(marker => candidate.Contains(marker, StringComparison.OrdinalIgnoreCase))).ToArray();
        return (real.Length > 0 ? real : candidates.ToArray()).OrderByDescending(candidate => candidate.Length).First();
    }

    private static string ReadMemory()
    {
        // The SMBIOS table is the only local source of the module type and
        // speed that laptops actually populate; WMI needs a package reference
        // and returns blank part numbers on this class of machine anyway.
        var modules = WidgetSmbios.Read();
        if (modules.Count == 0) return "";
        var total = modules.Sum(module => module.SizeBytes);
        var labelled = modules.FirstOrDefault(module => module.PartNumber.Length > 0)?.PartNumber
            ?? modules.FirstOrDefault(module => module.Manufacturer.Length > 0)?.Manufacturer
            ?? "";
        var descriptor = string.Join(" ", new[]
        {
            modules.FirstOrDefault(module => module.TypeName.Length > 0)?.TypeName ?? "",
            modules.FirstOrDefault(module => module.SpeedMhz > 0) is { } fastest ? fastest.SpeedMhz + "MHz" : ""
        }.Where(part => part.Length > 0));
        var size = total > 0
            ? string.Join(" ", new[] { descriptor, WidgetMonitorMetrics.Bytes(total) }.Where(part => part.Length > 0))
            : descriptor;
        if (labelled.Length == 0) return size;
        return size.Length == 0 ? labelled : labelled + " · " + size;
    }

    private static string ReadDisk()
    {
        // The registry identifier is the only place Windows exposes the model
        // without a WMI round trip. "0" is the system disk.
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\disk\Enum");
        return Normalize(ParseDiskIdentifier(key?.GetValue("0") as string));
    }

    /// <summary>
    /// Turns an enumerated disk identifier into a model name. The identifier
    /// looks like "SCSI\\Disk&Ven_NVMe&Prod_WD_PC_SN740_SDDP\\4&amp;...": the
    /// device class prefix carries no model information and the vendor repeats
    /// the product, so only the product segment is kept.
    /// </summary>
    public static string ParseDiskIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return "";
        var parts = identifier.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";
        var vendor = "";
        var product = "";
        foreach (var piece in parts[1].Split('&'))
        {
            var separator = piece.IndexOf('_');
            if (separator <= 0) continue;
            var label = piece[..separator];
            var value = piece[(separator + 1)..].Replace('_', ' ').Trim();
            if (label.Equals("Ven", StringComparison.OrdinalIgnoreCase)) vendor = value;
            else if (label.Equals("Prod", StringComparison.OrdinalIgnoreCase)) product = value;
        }
        // "NVMe" is both the vendor and the first product word, so prefixing it
        // would read "NVMe NVMe WD PC SN740". Keep the vendor only when the
        // product does not already say it.
        if (product.Length == 0) return vendor;
        return vendor.Length == 0 || product.Contains(vendor, StringComparison.OrdinalIgnoreCase)
            ? product
            : vendor + " " + product;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        foreach (var noise in new[] { "(R)", "(TM)", "(r)", "(tm)" })
        {
            text = text.Replace(noise, "");
        }
        return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
