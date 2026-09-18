using System;
using System.Runtime.InteropServices;
using LiteMon.Core.Models;

namespace LiteMon.Core.Interop;

/// <summary>
/// NativeWifi (wlanapi.dll) 最小 P/Invoke 封装：枚举接口、查询当前连接 / 信道 / 统计。
/// 只读操作；失败返回 null，绝不抛异常（由调用方降级）。
/// </summary>
internal static class WlanInterop
{
    private const int WLAN_MAX_NAME_LENGTH = 256;

    // ---------------- 结构 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct GUID
    {
        public uint Data1; public ushort Data2; public ushort Data3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Data4;
    }

    private enum WLAN_INTERFACE_STATE
    {
        NotReady = 0, Connected = 1, AdHocNetworkFormed = 2, Disconnecting = 3,
        Disconnected = 4, Associating = 5, Discovering = 6, Authenticating = 7,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public GUID InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WLAN_MAX_NAME_LENGTH)] public string Description;
        public WLAN_INTERFACE_STATE State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_INTERFACE_INFO_LIST
    {
        public uint NumberOfItems;
        public IntPtr Index;   // DWORD 索引，不用
        public IntPtr First;   // WLAN_INTERFACE_INFO[]
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint SSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] SSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID Ssid;       // @0  36
        public uint BssType;          // @36 4   dot11BssType
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;   // @40 6（pad 到 @48）
        public uint PhyType;          // @48 4   dot11PhyType 枚举：4=a 5=b 6=g 7=n 8=ac 10=ax 11=be
        public uint PhyIndex;         // @52 4   uDot11PhyIndex
        public uint SignalQuality;    // @56 4   0~100
        public uint RxRate;           // @60 4   Kbps
        public uint TxRate;           // @64 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        public int SecurityEnabled;   // BOOL 4
        public int OneXEnabled;       // BOOL 4
        public uint AuthAlgorithm;    // DOT11_AUTH_ALGORITHM 4
        public uint CipherAlgorithm;  // DOT11_CIPHER_ALGORITHM 4
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public WLAN_INTERFACE_STATE State;         // @0 4
        public uint ConnectionMode;               // @4 4
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WLAN_MAX_NAME_LENGTH)] public string ProfileName;   // @8 256
        public WLAN_ASSOCIATION_ATTRIBUTES Association;
        public WLAN_SECURITY_ATTRIBUTES Security;
    }

    // ---------------- 导出 ----------------

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern void WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr p);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr handle, ref GUID iface, int opcode, IntPtr reserved,
        out uint size, out IntPtr data, out int valueType);

    // WLAN_INTF_OPCODE（wlanapi.h）：autoconf_start=0, autoconf_enabled=1, background_scan_enabled=2,
    // media_streaming_mode=3, radio_state=4, bss_type=5, interface_state=6, current_connection=7, channel_number=8
    private const int OpcodeCurrentConnection = 7;    // wlan_intf_opcode_current_connection
    private const int OpcodeChannelNumber = 8;        // wlan_intf_opcode_channel_number
    private const int OpcodeStatistics = 0x10000101;  // wlan_intf_opcode_statistics

    private static readonly int SizeOfInterfaceInfo = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
    private static readonly int SizeOfConnection = Marshal.SizeOf<WLAN_CONNECTION_ATTRIBUTES>();

    // ---------------- 公共 API ----------------

    public sealed class WifiInfo
    {
        public required string Description { get; init; }
        public required bool IsConnected { get; init; }
        public string Ssid { get; init; } = "";
        public uint SignalPercent { get; init; }
        public uint Channel { get; init; }
        public uint RxLinkKbps { get; init; }   // 协商速率（链路），非实时吞吐
        public uint TxLinkKbps { get; init; }
        public ulong RxBytes { get; init; }     // 驱动累计接收字节（帧计数 x MAC 开销近似）
        public ulong TxBytes { get; init; }
        public string PhyType { get; init; } = "";  // 802.11 类型字符串
        public string StateText { get; init; } = ""; // 接口状态
    }

    public static List<LiteMon.Core.Models.NetworkInterfaceMetrics>? GetWifiInterfaces()
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out h) != 0) return null;
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) return null;
            var result = new List<LiteMon.Core.Models.NetworkInterfaceMetrics>();
            try
            {
                int n = Marshal.ReadInt32(list, 0);
                // 布局：DWORD dwNumberOfItems @0, DWORD dwIndex @4, WLAN_INTERFACE_INFO[] @8
                IntPtr p = (IntPtr)((long)list + 8);
                for (int i = 0; i < n; i++)
                {
                    var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>((IntPtr)((long)p + i * SizeOfInterfaceInfo));
                    result.Add(ReadInfo(h, info));
                }
            }
            finally { WlanFreeMemory(list); }
            return result;
        }
        catch { return null; }
        finally
        {
            if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
        }
    }

    private static NetworkInterfaceMetrics ReadInfo(IntPtr h, WLAN_INTERFACE_INFO info)
    {
        string ssid = "";
        bool connected = info.State == WLAN_INTERFACE_STATE.Connected;
        uint signal = 0, rxLink = 0, txLink = 0, channel = 0;
        ulong rxBytes = 0, txBytes = 0;
        string phy = "";

        // 当前连接（SSID/信号/协商速率）
        try
        {
            var guid = info.InterfaceGuid;
            if (WlanQueryInterface(h, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, out _) == 0)
            {
                try
                {
                    var c = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(data);
                    connected = c.State == WLAN_INTERFACE_STATE.Connected;
                    if (connected)
                    {
                        ssid = DecodeSsid(c.Association.Ssid);
                        signal = c.Association.SignalQuality;
                        rxLink = c.Association.RxRate;
                        txLink = c.Association.TxRate;
                        phy = PhyName(c.Association.PhyType);
                    }
                }
                finally { WlanFreeMemory(data); }
            }
        }
        catch { }

        // 信道
        try
        {
            var guid = info.InterfaceGuid;
            if (WlanQueryInterface(h, ref guid, OpcodeChannelNumber, IntPtr.Zero, out _, out var data, out _) == 0)
            {
                try { channel = (uint)Marshal.ReadInt32(data); }
                finally { WlanFreeMemory(data); }
            }
        }
        catch { }

        // 统计（累计收发帧数，作差分用）
        try
        {
            var guid = info.InterfaceGuid;
            if (WlanQueryInterface(h, ref guid, OpcodeStatistics, IntPtr.Zero, out _, out var data, out _) == 0)
            {
                try
                {
                    // WLAN_STATISTICS: 3*ULONGLONG + MacUcast(12*ULONGLONG) + MacMcast + ...
                    // MacUcast.ullTransmittedFrameCount @24, ullReceivedFrameCount @32
                    rxBytes = (ulong)Marshal.ReadInt64(data, 32);
                    txBytes = (ulong)Marshal.ReadInt64(data, 24);
                }
                finally { WlanFreeMemory(data); }
            }
        }
        catch { }

        return new NetworkInterfaceMetrics
        {
            Name = info.Description,
            Kind = InterfaceKind.Wifi,
            StateText = StateName(info.State),
            IsConnected = connected,
            Ssid = ssid,
            SignalPercent = signal,
            Channel = channel,
            PhyType = phy,
            RxLinkKbps = rxLink,
            TxLinkKbps = txLink,
            RxBytes = rxBytes,
            TxBytes = txBytes,
        };
    }

    private static string DecodeSsid(DOT11_SSID ssid)
    {
        int len = (int)Math.Min(ssid.SSIDLength, 32u);
        if (len <= 0 || ssid.SSID == null) return "";
        return System.Text.Encoding.UTF8.GetString(ssid.SSID, 0, len);
    }

    // 稳定英文码（语言无关）；UI 层按码查本地化文案
    private static string StateName(WLAN_INTERFACE_STATE s) => s switch
    {
        WLAN_INTERFACE_STATE.Connected => "connected",
        WLAN_INTERFACE_STATE.Disconnected => "disconnected",
        WLAN_INTERFACE_STATE.Disconnecting => "disconnecting",
        WLAN_INTERFACE_STATE.Associating => "associating",
        WLAN_INTERFACE_STATE.Discovering => "discovering",
        WLAN_INTERFACE_STATE.Authenticating => "authenticating",
        _ => "not-ready",
    };

    // dot11_phy_type 枚举（wlanapi）：fhss=1 dsss=2 ir=3 ofdm=4(11a) hrdsss=5(11b)
    // erp=6(11g) ht=7(11n) vht=8(11ac) dmg=9 he=10(11ax) eht=11(11be)
    private static string PhyName(uint phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "802.11n (Wi-Fi 4)",
        8 => "802.11ac (Wi-Fi 5)",
        10 => "802.11ax (Wi-Fi 6)",
        11 => "802.11be (Wi-Fi 7)",
        _ => "",
    };
}
