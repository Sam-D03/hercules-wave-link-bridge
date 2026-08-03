using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace HerculesWaveBridge;

internal static class DeviceInterfaceDiscovery
{
    private const string DeviceClassesKey = @"SYSTEM\CurrentControlSet\Control\DeviceClasses";
    private const string DeviceIdNeedle = "VID_06F8&PID_E053";
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private static readonly Guid WinUsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private static readonly Guid HerculesVendorMainInterface = new("ABEE8C27-397B-409D-B6DC-6161CA409D8D");
    private static readonly Guid HerculesVendorControlInterface = new("1A8758AA-4297-4D87-80BD-56BE721F66D0");
    private static readonly Guid ZadigInterfaceA = new("A88F02A1-0A06-46FF-91A4-9C85712B324B");
    private static readonly Guid ZadigInterfaceB = new("9226BF39-7D19-471E-BD34-F8FBB2E5F66F");

    public static IReadOnlyList<string> FindHerculesDevicePaths()
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var interfaceGuid in new[] { HerculesVendorMainInterface, HerculesVendorControlInterface, ZadigInterfaceA, ZadigInterfaceB, WinUsbDeviceInterface })
        {
            foreach (var path in EnumerateDeviceInterfacePaths(interfaceGuid))
            {
                AddIfHerculesPath(path, paths, seen);
            }
        }

        using var root = Registry.LocalMachine.OpenSubKey(DeviceClassesKey);
        if (root is null)
        {
            return paths;
        }

        foreach (var className in root.GetSubKeyNames())
        {
            using var classKey = root.OpenSubKey(className);
            if (classKey is null)
            {
                continue;
            }

            foreach (var childName in classKey.GetSubKeyNames())
            {
                if (childName.Contains(DeviceIdNeedle, StringComparison.OrdinalIgnoreCase) &&
                    childName.StartsWith("##?#", StringComparison.Ordinal))
                {
                    AddIfHerculesPath(@"\\.?\".Replace(".?", "?") + childName[4..], paths, seen);
                }
            }
        }

        return paths;
    }

    private static void AddIfHerculesPath(string path, List<string> paths, HashSet<string> seen)
    {
        if (!path.Contains(DeviceIdNeedle, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (seen.Add(path))
        {
            paths.Add(path);
        }
    }

    private static IEnumerable<string> EnumerateDeviceInterfacePaths(Guid interfaceGuid)
    {
        var deviceInfoSet = SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == new IntPtr(-1))
        {
            yield break;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    CbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };

                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData))
                {
                    yield break;
                }

                _ = SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
                if (requiredSize <= 0)
                {
                    continue;
                }

                var detailData = Marshal.AllocHGlobal(requiredSize);
                try
                {
                    Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailData, requiredSize, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    var pathPointer = IntPtr.Add(detailData, 4);
                    var path = Marshal.PtrToStringUni(pathPointer);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        yield return path;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailData);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }
}
