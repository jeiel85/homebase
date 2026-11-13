using System.Runtime.InteropServices;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Windows network category (Public / Private / Domain) per adapter, from the Network List
/// Manager. Declared interfaces rather than <c>dynamic</c>: the GUID-returning methods are not
/// automation-compatible, so late binding fails with E_INVALIDARG.
/// </summary>
internal static class NetworkCategory
{
    public const int Public = 0;

    /// <summary>(adapter GUID → (network name, category)) for every connected network.</summary>
    public static Dictionary<Guid, (string Name, int Category)> ByAdapter()
    {
        var result = new Dictionary<Guid, (string, int)>();
        var nlm = (INetworkListManager)new NetworkListManagerClass();
        try
        {
            Check(nlm.GetNetworkConnections(out var connections));
            var one = new INetworkConnection[1];
            while (connections.Next(1, one, out var fetched) == 0 && fetched == 1)
            {
                var connection = one[0];
                try
                {
                    Check(connection.GetAdapterId(out var adapter));
                    Check(connection.GetNetwork(out var network));
                    Check(network.GetName(out var name));
                    Check(network.GetCategory(out var category));
                    result[adapter] = (name, category);
                    Marshal.ReleaseComObject(network);
                }
                finally
                {
                    Marshal.ReleaseComObject(connection);
                }
            }
            Marshal.ReleaseComObject(connections);
        }
        finally
        {
            Marshal.ReleaseComObject(nlm);
        }
        return result;
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManagerClass { }

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [PreserveSig] int GetNetworks(int flags, [MarshalAs(UnmanagedType.Interface)] out object networks);
        [PreserveSig] int GetNetwork(Guid networkId, [MarshalAs(UnmanagedType.Interface)] out object network);
        [PreserveSig] int GetNetworkConnections(out IEnumNetworkConnections connections);
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [PreserveSig] int GetNewEnum([MarshalAs(UnmanagedType.Interface)] out object enumVariant);
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INetworkConnection[] elements, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [PreserveSig] int GetNetwork(out INetwork network);
        [PreserveSig] int get_IsConnectedToInternet(out short connected);
        [PreserveSig] int get_IsConnected(out short connected);
        [PreserveSig] int GetConnectivity(out int connectivity);
        [PreserveSig] int GetConnectionId(out Guid connectionId);
        [PreserveSig] int GetAdapterId(out Guid adapterId);
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.BStr)] out string name);
        [PreserveSig] int SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.BStr)] out string description);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        [PreserveSig] int GetNetworkId(out Guid networkId);
        [PreserveSig] int GetDomainType(out int domainType);
        [PreserveSig] int GetNetworkConnections([MarshalAs(UnmanagedType.Interface)] out object connections);
        [PreserveSig] int GetTimeCreatedAndConnected(out uint lowCreated, out uint highCreated, out uint lowConnected, out uint highConnected);
        [PreserveSig] int get_IsConnectedToInternet(out short connected);
        [PreserveSig] int get_IsConnected(out short connected);
        [PreserveSig] int GetConnectivity(out int connectivity);
        [PreserveSig] int GetCategory(out int category);
    }
}
