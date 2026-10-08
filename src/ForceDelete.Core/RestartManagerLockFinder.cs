using System.Runtime.InteropServices;

namespace ForceDelete.Core;

/// <summary>Uses the Windows Restart Manager to find processes holding a file open.</summary>
public sealed class RestartManagerLockFinder : ILockFinder
{
    private const int ERROR_MORE_DATA = 234;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0, RmMainWindow = 1, RmOtherWindow = 2,
        RmService = 3, RmExplorer = 4, RmConsole = 5, RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int flags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle,
        uint nFiles, string[] fileNames,
        uint nApplications, RM_UNIQUE_PROCESS[]? applications,
        uint nServices, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle,
        out uint procInfoNeeded, ref uint procInfo,
        [In, Out] RM_PROCESS_INFO[]? processInfo, ref uint rebootReasons);

    public IReadOnlyList<LockingProcess> FindLockers(string path)
    {
        // Restart Manager expects a normal path, not the \\?\ extended form.
        var key = Guid.NewGuid().ToString();
        if (RmStartSession(out uint session, 0, key) != 0)
            return Array.Empty<LockingProcess>();

        try
        {
            string[] resources = { path };
            if (RmRegisterResources(session, 1, resources, 0, null, 0, null) != 0)
                return Array.Empty<LockingProcess>();

            uint needed = 0, count = 0, reasons = 0;
            int res = RmGetList(session, out needed, ref count, null, ref reasons);
            if (res == 0) return Array.Empty<LockingProcess>();       // nothing holds it
            if (res != ERROR_MORE_DATA) return Array.Empty<LockingProcess>();

            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0)
                return Array.Empty<LockingProcess>();

            var list = new List<LockingProcess>();
            for (int i = 0; i < count; i++)
            {
                int pid = infos[i].Process.dwProcessId;
                string name = infos[i].strAppName ?? string.Empty;
                bool critical = infos[i].ApplicationType == RM_APP_TYPE.RmCritical
                                || CriticalProcessGuard.IsCritical(pid, name);
                list.Add(new LockingProcess(pid, name, critical));
            }
            return list;
        }
        finally
        {
            RmEndSession(session);
        }
    }
}
