using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VSTerrainDiffusion.Core;

/// <summary>Windows: display adapters from DXGI, and whether Direct3D 12 can make a device.</summary>
public readonly partial struct GpuInfo
{
    /// <summary>
    /// DXGI and Direct3D 12, called through their flat exports and COM vtables without a wrapper library.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class Dxgi
    {
        /// <summary>IID of IDXGIFactory1.</summary>
        private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

        /// <summary>IID of ID3D12Device.</summary>
        private static readonly Guid IidD3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");

        /// <summary>D3D_FEATURE_LEVEL_11_0, the least DirectML needs.</summary>
        private const int D3DFeatureLevel11_0 = 0xB000;

        /// <summary>DXGI_ADAPTER_FLAG_SOFTWARE: the adapter is a software rasteriser.</summary>
        private const uint DxgiAdapterFlagSoftware = 2;

        /// <summary>Most adapters enumerated, as a guard.</summary>
        private const uint MaxAdapters = 64;

        /// <summary>DXGI_ADAPTER_DESC1.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDesc1
        {
            /// <summary>The adapter's name.</summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;

            /// <summary>PCI vendor id.</summary>
            public uint VendorId;

            /// <summary>PCI device id.</summary>
            public uint DeviceId;

            /// <summary>PCI subsystem id.</summary>
            public uint SubSysId;

            /// <summary>PCI revision.</summary>
            public uint Revision;

            /// <summary>
            /// Video memory not shared with the CPU, in bytes; ranks adapters from one vendor.
            /// </summary>
            public nuint DedicatedVideoMemory;

            /// <summary>System memory reserved for the adapter, in bytes.</summary>
            public nuint DedicatedSystemMemory;

            /// <summary>System memory the adapter may share, in bytes.</summary>
            public nuint SharedSystemMemory;

            /// <summary>Low half of the adapter's locally unique id.</summary>
            public uint AdapterLuidLow;

            /// <summary>High half of the adapter's locally unique id.</summary>
            public int AdapterLuidHigh;

            /// <summary>DXGI_ADAPTER_FLAG values.</summary>
            public uint Flags;
        }

        /// <summary>IDXGIFactory1::EnumAdapters1.</summary>
        /// <param name="factory">The factory.</param>
        /// <param name="index">Adapter index.</param>
        /// <param name="adapter">Receives the adapter, which the caller releases.</param>
        /// <returns>An HRESULT; DXGI_ERROR_NOT_FOUND past the last.</returns>
        private delegate int EnumAdapters1Fn(IntPtr factory, uint index, out IntPtr adapter);

        /// <summary>IDXGIAdapter1::GetDesc1.</summary>
        /// <param name="adapter">The adapter.</param>
        /// <param name="desc">Receives its description.</param>
        /// <returns>An HRESULT.</returns>
        private delegate int GetDesc1Fn(IntPtr adapter, out AdapterDesc1 desc);

        /// <summary>Creates a DXGI factory.</summary>
        /// <param name="riid">The interface wanted.</param>
        /// <param name="factory">Receives it.</param>
        /// <returns>An HRESULT.</returns>
        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        /// <summary>
        /// Creates a Direct3D 12 device, or with a null output only checks that one could be.
        /// </summary>
        /// <param name="adapter">The adapter, or null for the default.</param>
        /// <param name="minimumFeatureLevel">The least feature level accepted.</param>
        /// <param name="riid">The interface wanted.</param>
        /// <param name="device">Receives the device, or null to only check.</param>
        /// <returns>An HRESULT; S_FALSE when only checking and it would succeed.</returns>
        [DllImport("d3d12.dll")]
        private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, IntPtr device);

        /// <summary>Adapters that are present now; the registry keeps entries for cards long removed.</summary>
        public static List<Adapter> Adapters()
        {
            var adapters = new List<Adapter>();
            Guid iid = IidDxgiFactory1;
            if (CreateDXGIFactory1(ref iid, out IntPtr factory) < 0 || factory == IntPtr.Zero) return adapters;
            try
            {
                // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1 by vtable slot.
                var enumAdapters = ComMethod<EnumAdapters1Fn>(factory, 12);
                for (uint i = 0; i < MaxAdapters && enumAdapters(factory, i, out IntPtr adapter) >= 0; i++)
                {
                    if (adapter == IntPtr.Zero) continue;
                    try
                    {
                        if (ComMethod<GetDesc1Fn>(adapter, 10)(adapter, out AdapterDesc1 desc) < 0) continue;
                        // The Basic Render Driver is a CPU rasteriser.
                        if ((desc.Flags & DxgiAdapterFlagSoftware) != 0) continue;
                        adapters.Add(new Adapter(ManufacturerFor((int)desc.VendorId), desc.Description?.Trim(),
                            (int)desc.DeviceId, (long)desc.DedicatedVideoMemory));
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
            return adapters;
        }

        /// <summary>Asks for a device without anywhere to put it, which only tests that one could be made.</summary>
        public static bool SupportsDirect3D12()
        {
            try
            {
                Guid iid = IidD3D12Device;
                return D3D12CreateDevice(IntPtr.Zero, D3DFeatureLevel11_0, ref iid, IntPtr.Zero) >= 0;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        /// <summary>A COM method by its vtable slot.</summary>
        /// <typeparam name="T">The method's delegate type.</typeparam>
        /// <param name="self">The interface pointer.</param>
        /// <param name="slot">The slot, counting IUnknown's three.</param>
        /// <returns>A callable delegate.</returns>
        private static T ComMethod<T>(IntPtr self, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));
    }
}
