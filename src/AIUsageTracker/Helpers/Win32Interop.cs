using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace AIUsageTracker.Helpers;

public static class Win32Interop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tblClass,
        uint reserved = 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] localPort;
        public uint remoteAddr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] remotePort;
        public uint owningPid;
    }

    private static long GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex).ToInt64()
            : GetWindowLong32(hWnd, nIndex);
    }

    private static void SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hWnd, nIndex, new IntPtr(dwNewLong));
        }
        else
        {
            SetWindowLong32(hWnd, nIndex, (int)dwNewLong);
        }
    }

    public static void SetToolWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, exStyle);
    }

    public static void SetClickThrough(IntPtr hWnd, bool enable)
    {
        if (hWnd == IntPtr.Zero) return;
        long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        if (enable)
        {
            exStyle |= WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
        }
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, exStyle);
    }

    /// <summary>
    /// Reads the command line of a process owned by the current user via NtQueryInformationProcess (ProcessCommandLineInformation = 60).
    /// Fast, native, and requires no external dependencies or WMI overhead.
    /// </summary>
    public static string? GetProcessCommandLine(int processId)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            int status = NtQueryInformationProcess(hProcess, ProcessCommandLineInformation, IntPtr.Zero, 0, out int returnLength);
            if (returnLength <= 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal(returnLength);
            try
            {
                status = NtQueryInformationProcess(hProcess, ProcessCommandLineInformation, buffer, returnLength, out _);
                if (status != 0)
                {
                    return null;
                }

                UNICODE_STRING unicodeString = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
                if (unicodeString.Buffer == IntPtr.Zero || unicodeString.Length == 0)
                {
                    return string.Empty;
                }

                return Marshal.PtrToStringUni(unicodeString.Buffer, unicodeString.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Returns all active IPv4 TCP listening ports owned by the specified process ID.
    /// </summary>
    public static List<int> GetListeningPortsForPid(int processId)
    {
        var ports = new List<int>();
        int bufferSize = 0;

        uint result = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER);
        if (bufferSize <= 0)
        {
            return ports;
        }

        IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedTcpTable(tcpTablePtr, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER);
            if (result != 0)
            {
                return ports;
            }

            int rowCount = Marshal.ReadInt32(tcpTablePtr);
            IntPtr rowPtr = IntPtr.Add(tcpTablePtr, sizeof(int));
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                if (row.owningPid == (uint)processId)
                {
                    int port = (row.localPort[0] << 8) + row.localPort[1];
                    if (!ports.Contains(port))
                    {
                        ports.Add(port);
                    }
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        catch
        {
            // Ignore errors and return collected ports
        }
        finally
        {
            Marshal.FreeHGlobal(tcpTablePtr);
        }

        return ports;
    }
}
