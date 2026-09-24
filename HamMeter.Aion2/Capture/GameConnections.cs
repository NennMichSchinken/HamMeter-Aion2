using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace HamMeter.Capture;

// One established IPv4 TCP connection, addresses in network byte order as Windows
// reports them (so they compare directly against the raw IP header bytes).
public readonly record struct TcpConnection(uint LocalAddress, ushort LocalPort, uint RemoteAddress, ushort RemotePort)
{
    public IPAddress Local => new(this.LocalAddress);
}

// Lists the TCP connections owned by the Aion 2 client. The raw-socket capture only
// accepts packets that belong to one of these, so traffic of every other program on
// the machine is dropped before any of its bytes are looked at.
public static class GameConnections
{
    private const string ProcessName = "Aion2";

    public static List<TcpConnection> Find()
    {
        HashSet<int> pids = new();
        foreach (Process p in Process.GetProcessesByName(ProcessName))
        {
            pids.Add(p.Id);
            p.Dispose();
        }

        List<TcpConnection> result = new();
        if (pids.Count == 0)
        {
            return result;
        }

        foreach (Row row in ReadTable())
        {
            if (row.State != MibTcpStateEstab || !pids.Contains((int)row.OwningPid))
            {
                continue;
            }

            // Loopback connections (VPN/booster tunnels) never reach a raw socket.
            if ((row.RemoteAddr & 0xFF) == 127)
            {
                continue;
            }

            result.Add(new TcpConnection(row.LocalAddr, Port(row.LocalPort), row.RemoteAddr, Port(row.RemotePort)));
        }

        return result;
    }

    // The table stores ports in network byte order in the low 16 bits.
    private static ushort Port(uint raw) => (ushort)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static List<Row> ReadTable()
    {
        List<Row> rows = new();
        int size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint rc = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
                if (rc == ErrorInsufficientBuffer)
                {
                    continue; // table grew between the calls; retry with the new size
                }

                if (rc != 0)
                {
                    return rows;
                }

                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<Row>();
                IntPtr ptr = buffer + 4;
                for (int i = 0; i < count; i++)
                {
                    rows.Add(Marshal.PtrToStructure<Row>(ptr));
                    ptr += rowSize;
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return rows;
    }

    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const uint MibTcpStateEstab = 5;
    private const uint ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct Row
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}
