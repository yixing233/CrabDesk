using CrabDesk.Core;
using CrabDesk.Native;
using Xunit;

namespace CrabDesk.Tests;

/// <summary>
/// Hardware names decorate the performance card, so every parser here has to
/// stay total: firmware that omits a field must produce a blank name, never an
/// exception or a nonsense value.
/// </summary>
public sealed class WidgetHardwareTests
{
    [Theory]
    // The enumerated identifier puts the vendor in a label and repeats it in
    // the product; only the product should survive.
    [InlineData("SCSI\\Disk&Ven_NVMe&Prod_WD_PC_SN740_SDDP\\4&387ba32e&0&020000", "NVMe WD PC SN740 SDDP")]
    [InlineData("SCSI\\Disk&Ven_NVMe&Prod_KIOXIA-EXCERIA_P\\4&387ba32e&0&030000", "NVMe KIOXIA-EXCERIA P")]
    // A vendor the product already repeats is dropped instead of doubled.
    [InlineData("SCSI\\Disk&Ven_NVMe&Prod_NVMe_SSD_990\\4&1&0", "NVMe SSD 990")]
    // A vendor that the product does not repeat is worth keeping.
    [InlineData("SCSI\\Disk&Ven_Samsung&Prod_SSD_980_PRO\\4&1&0", "Samsung SSD 980 PRO")]
    [InlineData("", "")]
    [InlineData("nonsense", "")]
    public void DiskIdentifierBecomesAModelName(string identifier, string expected) =>
        Assert.Equal(expected, WidgetHardwareInventory.ParseDiskIdentifier(identifier));

    [Theory]
    [InlineData(0x1A, "DDR4")]
    [InlineData(0x22, "DDR5")]
    [InlineData(0x13, "DDR2")]
    [InlineData(0x18, "DDR3")]
    [InlineData(0x23, "LPDDR5")]
    [InlineData(0x24, "HBM3")]
    // Reserved and vendor-specific codes must not leak a number onto the card.
    [InlineData(0x01, "")]
    [InlineData(0x02, "")]
    [InlineData(0x7E, "")]
    [InlineData(0xFF, "")]
    public void MemoryTypeCodesMapToNamesOrNothing(byte code, string expected) =>
        Assert.Equal(expected, WidgetSmbios.MemoryTypeName(code));

    [Fact]
    public void SmbiosParserReadsModulesAndSkipsSlotTerminators()
    {
        // Two Type 17 records: one populated DDR5 module and one empty slot,
        // followed by the end-of-table marker. Sizes are in megabytes.
        var populated = Type17(sizeMb: 16384, speedMhz: 4800, typeCode: 0x22, slot: "DIMM0", part: "CMK32GX5M2B5600");
        var empty = Type17(sizeMb: 0, speedMhz: 0, typeCode: 0x02, slot: "DIMM1", part: "");
        var table = populated.Concat(empty).Concat(new byte[] { 127, 4, 0, 0 }).ToArray();

        var modules = WidgetSmbios.Parse(table);

        var module = Assert.Single(modules);
        Assert.Equal("CMK32GX5M2B5600", module.PartNumber);
        Assert.Equal("DDR5", module.TypeName);
        Assert.Equal(4800, module.SpeedMhz);
        Assert.Equal(16384L * 1024 * 1024, module.SizeBytes);
    }

    [Fact]
    public void SmbiosParserReturnsNothingForATruncatedTable()
    {
        var record = Type17(sizeMb: 8192, speedMhz: 3200, typeCode: 0x1A, slot: "DIMM0", part: "X");
        Assert.Empty(WidgetSmbios.Parse(record.AsSpan(0, 6)));
        Assert.Empty(WidgetSmbios.Parse(ReadOnlySpan<byte>.Empty));
    }

    // A Type 17 structure with a filled formatted area plus its string set.
    private static byte[] Type17(int sizeMb, int speedMhz, byte typeCode, string slot, string part)
    {
        var entries = new List<string> { slot, "Manufacturer" };
        if (part.Length > 0) entries.Add(part);
        var strings = string.Join('\0', entries) + "\0\0";
        var formatted = new byte[0x54];
        formatted[0x00] = 17;
        formatted[0x01] = 0x54;
        formatted[0x0C] = (byte)(sizeMb & 0xFF);
        formatted[0x0D] = (byte)(sizeMb >> 8 & 0xFF);
        formatted[0x12] = typeCode;
        formatted[0x15] = (byte)(speedMhz & 0xFF);
        formatted[0x16] = (byte)(speedMhz >> 8 & 0xFF);
        formatted[0x17] = 2; // manufacturer
        formatted[0x18] = 3; // part number
        return formatted.Concat(System.Text.Encoding.ASCII.GetBytes(strings)).ToArray();
    }

    // The GPU temperature comes from NVML rather than an external process: a
    // child process costs ~140 ms per call, far too slow for a one-second
    // sampler. On a machine without an NVIDIA adapter this must return null
    // instead of throwing, so the rest of the sample survives.
    [Fact]
    public void GpuTemperatureEitherReadsAPlausibleValueOrReportsNothing()
    {
        using var reader = new WidgetGpuTemperature();

        var first = reader.Read();
        var second = reader.Read();

        if (first is { } celsius)
        {
            Assert.InRange(celsius, -50, 200);
            // Repeated reads must be stable and must not reinitialise the driver
            // into a different device on every call.
            Assert.Equal(first, second);
        }
        else
        {
            Assert.Null(second);
        }
    }

    [Fact]
    public void GpuTemperatureToleratesUseAfterDispose()
    {
        var reader = new WidgetGpuTemperature();
        reader.Dispose();

        Assert.Null(reader.Read());
    }

    // The cell that shows a model name is a fixed pixel width, but the label cap
    // counts characters, so a name inside the cap can still be clipped by the
    // renderer. Dropping the vendor buys the room the model number needs.
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", "GeForce RTX 4060 Laptop GPU")]
    [InlineData("AMD Radeon RX 7900 XTX", "Radeon RX 7900 XTX")]
    [InlineData("12th Gen Intel Core i7-12700H", "12th Gen Intel Core i7-12700H")]
    public void VendorPrefixIsDroppedSoTheModelNumberSurvives(string model, string expected)
    {
        Assert.Equal(expected, WidgetMonitorMetrics.HardwareLabel(model));
    }

    [Fact]
    public void OverlongModelNamesStillEndWithAnEllipsis()
    {
        var label = WidgetMonitorMetrics.HardwareLabel(
            "Some Extremely Long Graphics Adapter Product Name That Cannot Fit", 20);

        Assert.EndsWith("…", label);
        Assert.True(label.Length <= 20);
    }

    // Internal panels use WMI; external monitors use DDC/CI. Report the source
    // actually used, or unavailable when no panel supports brightness control.
    [Fact]
    public void BrightnessReportsWhichSourceItUsed()
    {
        var reading = BrightnessControl.Read();

        if (reading.Percent is { } percent)
        {
            Assert.InRange(percent, 0, 100);
            // Only the two known mechanisms; a third would mean an unhandled path.
            Assert.Contains(reading.Source, new[] { "ddc", "wmi" });
        }
        else
        {
            Assert.Equal("unavailable", reading.Source);
        }
    }

    [Fact]
    public void BrightnessRoundTripsThroughItsPreferredSource()
    {
        // The default WMI panel may belong to a closed laptop lid. Exercise an
        // active display, using the same explicit target as the control centre.
        var displayId = ActiveDisplayPaths.Read().Keys.FirstOrDefault(id => BrightnessControl.Read(id).Percent is not null);
        if (displayId is null) return; // No active dimmable monitor here.
        var original = BrightnessControl.Read(displayId);
        if (original.Percent is not { } start) return;

        try
        {
            var target = start > 50 ? 40d : 60d;
            Assert.True(BrightnessControl.Write(target, displayId));
            var applied = WaitForBrightness(target, displayId);
            // The panel applies a DDC write asynchronously and some monitors round
            // slightly, so allow a small tolerance rather than an exact match.
            Assert.NotNull(applied.Percent);
            Assert.InRange(applied.Percent!.Value, target - 5, target + 5);
        }
        finally
        {
            BrightnessControl.Write(start, displayId);
            // Leaving the screen dimmed after a test run would be a nasty surprise.
            Assert.InRange(WaitForBrightness(start, displayId).Percent ?? start, start - 5, start + 5);
        }
    }

    private static BrightnessControl.Reading WaitForBrightness(double expected, string displayId)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        BrightnessControl.Reading reading;
        do
        {
            reading = BrightnessControl.Read(displayId);
            if (reading.Percent is { } percent && Math.Abs(percent - expected) <= 5) return reading;
            Thread.Sleep(100);
        } while (timer.Elapsed < TimeSpan.FromSeconds(5));
        return reading;
    }
}
