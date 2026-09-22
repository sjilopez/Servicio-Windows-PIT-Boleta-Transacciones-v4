using System.Runtime.InteropServices;

namespace PITBoletaTransacciones.Services;

internal static class WindowsSessionUser
{
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private const int WtsActive = 0;

    public static string? GetActiveUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IntPtr server = IntPtr.Zero;
        if (!WTSEnumerateSessions(server, 0, 1, out IntPtr sessions, out int count))
        {
            return null;
        }

        try
        {
            int dataSize = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int index = 0; index < count; index++)
            {
                WTS_SESSION_INFO session = Marshal.PtrToStructure<WTS_SESSION_INFO>(sessions + index * dataSize);
                if (session.State != WtsActive || !WTSQuerySessionInformation(server, session.SessionId, WtsUserName, out IntPtr userBuffer, out int userLength))
                {
                    continue;
                }

                try
                {
                    string user = Marshal.PtrToStringUni(userBuffer, userLength / 2 - 1) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(user))
                    {
                        return user;
                    }
                }
                finally
                {
                    WTSFreeMemory(userBuffer);
                }
            }
        }
        finally
        {
            WTSFreeMemory(sessions);
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("Wtsapi32.dll")]
    private static extern bool WTSEnumerateSessions(IntPtr serverHandle, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr serverHandle, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}