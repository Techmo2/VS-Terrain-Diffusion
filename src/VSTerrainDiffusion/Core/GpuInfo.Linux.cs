using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VSTerrainDiffusion.Core;

/// <summary>Linux: display adapters from sysfs and the PCI ID database, and ROCm's GPU target from its kernel topology.</summary>
public readonly partial struct GpuInfo
{
    /// <summary>
    /// The first GPU in ROCm's kernel topology, as the LLVM target its runtime compiles for. Each
    /// node's <c>gfx_target_version</c> is major * 10000 + minor * 100 + stepping, with minor and
    /// stepping written in hex in the target name (90010 is gfx90a); CPU nodes read 0. Null when
    /// the amdgpu driver exposes no GPU to ROCm, or ROCm's device node cannot be opened.
    /// </summary>
    private static string KfdGfxTarget()
    {
        const string root = "/sys/class/kfd/kfd/topology/nodes";
        if (!Directory.Exists(root) || !File.Exists("/dev/kfd")) return null;

        try
        {
            // ROCm opens it read-write, and a user outside the render group cannot.
            using (new FileStream("/dev/kfd", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var nodes = new List<int>();
        foreach (string node in Directory.EnumerateDirectories(root))
            if (int.TryParse(Path.GetFileName(node), out int index)) nodes.Add(index);
        nodes.Sort();

        foreach (int node in nodes)
        {
            string properties = Path.Combine(root, node.ToString(CultureInfo.InvariantCulture), "properties");
            if (!File.Exists(properties)) continue;
            foreach (string line in File.ReadLines(properties))
            {
                if (!line.StartsWith("gfx_target_version ", StringComparison.Ordinal)) continue;
                if (!int.TryParse(line.AsSpan("gfx_target_version ".Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int version) || version == 0) break;
                return $"gfx{version / 10000}{version / 100 % 100:x}{version % 100:x}";
            }
        }
        return null;
    }

    /// <summary>Every display controller on the PCI bus, from sysfs.</summary>
    /// <returns>The adapters, possibly none.</returns>
    private static List<Adapter> LinuxAdapters()
    {
        var adapters = new List<Adapter>();
        const string root = "/sys/bus/pci/devices";
        if (!Directory.Exists(root)) return adapters;

        foreach (string device in Directory.EnumerateFileSystemEntries(root))
        {
            try
            {
                // PCI class 0x03xxxx is a display controller: VGA, 3D or other.
                if (!File.ReadAllText(Path.Combine(device, "class")).Trim().StartsWith("0x03", StringComparison.Ordinal))
                    continue;
                int vendor = ParseHex(File.ReadAllText(Path.Combine(device, "vendor")));
                int deviceId = ParseHex(File.ReadAllText(Path.Combine(device, "device")));

                // amdgpu fills product_name from the VBIOS on some cards; pci.ids covers the rest.
                string productName = Path.Combine(device, "product_name");
                string name = File.Exists(productName) ? File.ReadAllText(productName).Trim() : null;
                if (string.IsNullOrEmpty(name)) name = PciIdsName(vendor, deviceId);
                name ??= $"{ManufacturerFor(vendor)} device 0x{deviceId:x4}";

                // Intel's iGPU is always 0000:00:02.0, on the root bus; a discrete card sits behind a bridge.
                string address = Path.GetFileName(device);
                long tieBreak = address.Length >= 7 && address.Substring(5, 2) != "00" ? 1 : 0;

                adapters.Add(new Adapter(ManufacturerFor(vendor), name, deviceId, tieBreak));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
        }
        return adapters;
    }

    /// <summary>Parses sysfs's "0x10de" style hex.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The value.</returns>
    private static int ParseHex(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
        return int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>The device's name from the system's copy of the PCI ID database, if it has one.</summary>
    private static string PciIdsName(int vendor, int device)
    {
        foreach (string path in new[] { "/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids" })
        {
            if (!File.Exists(path)) continue;

            // Vendors are "10de  NVIDIA Corporation"; their devices follow as "\t2684  AD102 [GeForce RTX 4090]".
            string vendorPrefix = vendor.ToString("x4") + " ";
            string devicePrefix = "\t" + device.ToString("x4") + " ";
            bool inVendor = false;
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] != '\t')
                {
                    if (inVendor) return null;
                    inVendor = line.StartsWith(vendorPrefix, StringComparison.Ordinal);
                }
                else if (inVendor && line.StartsWith(devicePrefix, StringComparison.Ordinal))
                {
                    return line.Substring(devicePrefix.Length).Trim();
                }
            }
            return null;
        }
        return null;
    }
}
