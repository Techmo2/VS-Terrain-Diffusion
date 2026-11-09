using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace VSTerrainDiffusion.Core;

/// <summary>macOS: display adapters from <c>system_profiler</c>.</summary>
public readonly partial struct GpuInfo
{
    /// <summary>Every GPU <c>system_profiler SPDisplaysDataType</c> lists.</summary>
    /// <returns>The adapters, possibly none.</returns>
    private static List<Adapter> MacAdapters()
    {
        var adapters = new List<Adapter>();
        string output = RunProcess("/usr/sbin/system_profiler", "SPDisplaysDataType");
        if (output == null) return adapters;

        // One block per GPU:
        //   Chipset Model: AMD Radeon Pro 5500M
        //   Bus: PCIe
        //   Vendor: AMD (0x1002)
        //   Device ID: 0x7340
        string name = null;
        var manufacturer = GpuManufacturer.Unknown;
        int deviceId = 0;
        bool builtIn = false;
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line.Substring(0, colon), value = line.Substring(colon + 1).Trim();

            if (key == "Chipset Model")
            {
                if (name != null) adapters.Add(new Adapter(manufacturer, name, deviceId, builtIn ? 0 : 1));
                name = value;
                manufacturer = GpuManufacturer.Unknown;
                deviceId = 0;
                builtIn = false;
            }
            else if (key == "Bus") builtIn = value == "Built-In";
            else if (key == "Vendor") manufacturer = MacVendor(value);
            else if (key == "Device ID") deviceId = TryParseHexToken(value);
        }
        if (name != null) adapters.Add(new Adapter(manufacturer, name, deviceId, builtIn ? 0 : 1));
        return adapters;
    }

    /// <summary>The manufacturer from system_profiler's Vendor line.</summary>
    /// <param name="vendor">The line's value.</param>
    /// <returns>The manufacturer.</returns>
    private static GpuManufacturer MacVendor(string vendor)
    {
        // "Apple (0x106b)", "Intel", and on some releases "sppci_vendor_Apple".
        int vendorId = TryParseHexToken(vendor);
        if (vendorId != 0) return ManufacturerFor(vendorId);

        string lower = vendor.ToLowerInvariant();
        if (lower.Contains("nvidia")) return GpuManufacturer.Nvidia;
        if (lower.Contains("amd") || lower.Contains("ati")) return GpuManufacturer.Amd;
        if (lower.Contains("intel")) return GpuManufacturer.Intel;
        if (lower.Contains("apple")) return GpuManufacturer.Apple;
        return GpuManufacturer.Unknown;
    }

    /// <summary>The four hex digits after the first "0x" in <paramref name="text"/>, or 0.</summary>
    private static int TryParseHexToken(string text)
    {
        int start = text.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || text.Length < start + 6) return 0;
        return int.TryParse(text.Substring(start + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
    }

    /// <summary>Runs a program and returns its output, giving up after ten seconds.</summary>
    /// <param name="fileName">The program.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>Standard output, or null if it failed or timed out.</returns>
    private static string RunProcess(string fileName, string arguments)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return output.Result;
        }
        catch { return null; }
    }
}
