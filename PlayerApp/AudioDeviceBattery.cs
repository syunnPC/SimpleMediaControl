using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace PlayerApp;

// Read Windows' cached PnP properties; do not connect to or probe Bluetooth radios.
internal static class AudioDeviceBattery
{
    private static readonly PropertyKey Container = new(new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly PropertyKey Battery = new(new("49CD1F76-5626-4B17-A4E8-18B4AA1A2213"), 10);
    // Battery percentage published by the Windows Bluetooth device provider.
    private static readonly PropertyKey BluetoothBattery = new(new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly Guid ComputerContainer = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    public static bool IsPeripheral(Guid container) => container != Guid.Empty && container != ComputerContainer;

    public static int? ReadPercentage(Guid container)
    {
        if (!IsPeripheral(container))
        {
            return null;
        }

        const uint present = 0x100;
        // Topology can change between querying the size and reading the list.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var length, null, present) != 0)
            {
                return null;
            }

            var buffer = new char[length];
            var result = CM_Get_Device_ID_ListW(null, buffer, length, present);
            if (result == 0x1A)
            {
                continue; // CR_BUFFER_SMALL
            }

            if (result != 0)
            {
                return null;
            }

            int? lowest = null;
            foreach (var instance in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (CM_Locate_DevNodeW(out var node, instance, 0) != 0)
                {
                    continue;
                }

                var id = Read(node, Container, out var type);
                if (type != 0x0D || id?.Length != 16 || new Guid(id) != container)
                {
                    continue;
                }

                foreach (var key in new[] { Battery, BluetoothBattery })
                {
                    var value = ParsePercentage(Read(node, key, out type), type);
                    if (value is { } percent)
                    {
                        lowest = Math.Min(lowest ?? 100, percent);
                    }
                }
            }
            return lowest;
        }
        return null;
    }

    internal static int? ParsePercentage(byte[]? value, uint type) =>
        type == 0x03 && value is { Length: 1 } && value[0] <= 100 ? value[0] : null;

    private static byte[]? Read(uint node, PropertyKey key, out uint type)
    {
        uint length = 0;
        var result = CM_Get_DevNode_PropertyW(node, ref key, out type, null, ref length, 0);
        if (result != 0x1A || length > 4096)
        {
            return null;
        }

        var buffer = new byte[length];
        return CM_Get_DevNode_PropertyW(node, ref key, out type, buffer, ref length, 0) == 0
            ? buffer.Take((int)length).ToArray() : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey key, out uint type,
        [Out] byte[]? buffer, ref uint length, uint flags);
}
