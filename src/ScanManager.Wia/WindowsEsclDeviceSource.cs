using System.Runtime.InteropServices;
using System.Text;
using ScanManager.Core.Scanning;
using ScanManager.Escl;

namespace ScanManager.Wia;

/// <summary>
/// Finds the network scanners Windows has already installed with its eSCL class driver
/// (escl.inf) and reads the eSCL address the driver stores on each device node. Using
/// Windows' own discovery means no mDNS code, and the device ids match the WIA ones,
/// so the same scanner isn't listed twice.
/// </summary>
public sealed class WindowsEsclDeviceSource : IEsclDeviceSource
{
    private const string EsclInstancePrefix = @"SWD\ESCL\";

    // DEVPKEY_NAME (the display name; eSCL device nodes have no FriendlyName) and DEVPKEY_Device_DeviceDesc.
    private static readonly DevPropKey NameKey = new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);
    private static readonly DevPropKey DeviceDescKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

    // Set by the eSCL class driver: the device's eSCL root, e.g. "http://192.168.0.7:80/eSCL".
    private static readonly DevPropKey EsclUriKey = new(new Guid("a35996ab-11cf-4935-8b61-a6761081ecdf"), 12);

    public Task<IReadOnlyList<EsclDevice>> FindDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<EsclDevice>>(FindDevices, cancellationToken);

    private static List<EsclDevice> FindDevices()
    {
        var result = new List<EsclDevice>();
        foreach (var instanceId in PresentDeviceIds("SWD"))
        {
            if (!instanceId.StartsWith(EsclInstancePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != CrSuccess) continue;

            string? uri = GetStringProperty(node, EsclUriKey);
            if (uri is null || !Uri.TryCreate(uri, UriKind.Absolute, out var baseUri)) continue;
            if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) continue;

            string name = DeviceNames.Clean(GetStringProperty(node, NameKey) ?? GetStringProperty(node, DeviceDescKey) ?? baseUri.Host);
            result.Add(new EsclDevice(instanceId, name, baseUri));
        }
        return result;
    }

    private static IEnumerable<string> PresentDeviceIds(string enumerator)
    {
        const int flags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;
        if (CM_Get_Device_ID_List_SizeW(out int length, enumerator, flags) != CrSuccess || length <= 1)
            return [];
        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(enumerator, buffer, length, flags) != CrSuccess)
            return [];
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? GetStringProperty(uint node, DevPropKey key)
    {
        int size = 0;
        CM_Get_DevNode_PropertyW(node, ref key, out _, null, ref size, 0);
        if (size <= 2) return null;
        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(node, ref key, out uint type, buffer, ref size, 0) != CrSuccess || type != DevPropTypeString)
            return null;
        return Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0');
    }

    private const int CrSuccess = 0;
    private const int CmGetIdListFilterEnumerator = 0x1;
    private const int CmGetIdListFilterPresent = 0x100;
    private const uint DevPropTypeString = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int length, string filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, int length, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Locate_DevNodeW(out uint node, string instanceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint type, byte[]? buffer, ref int size, int flags);
}
