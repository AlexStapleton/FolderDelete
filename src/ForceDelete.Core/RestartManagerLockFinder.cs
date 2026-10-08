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
        path = PathUtil.FromExtendedPath(path);
        var key = Guid.NewGuid().ToString();
        if (RmStartSession(out uint session, 0, key) != 0)
            return Array.Empty<LockingProcess>();

        try
        {
            string[] resources = { path };
            if (RmRegisterResources(session, 1, resources, 0, null, 0, null) != 0)
                return Array.Empty<LockingProcess>();

            uint needed = 0, count = 0, reasons = 0;
            RM_PROCESS_INFO[]? infos = null;
            int res = RmGetList(session, out needed, ref count, null, ref reasons);
            // The holder list can grow between calls; re-size and retry a few times.
            for (int i = 0; i < 3 && res == ERROR_MORE_DATA; i++)
            {
                infos = new RM_PROCESS_INFO[needed];
                count = needed;
                res = RmGetList(session, out needed, ref count, infos, ref reasons);
            }
            if (res != 0 || infos is null) return Array.Empty<LockingProcess>(); // nothing holds it, or error

            var list = new List<LockingProcess>();
            for (int i = 0; i < count; i++)
            {
                var info = infos[i];
                int pid = info.Process.dwProcessId;

                if (!ProcessInspector.TryInspect(pid, out var snapshot))
                    continue; // exited since Restart Manager saw it — no longer a holder

                // strAppName is a display name ("Microsoft Word"), so the name guard
                // must check the real executable name instead.
                bool isService = info.ApplicationType == RM_APP_TYPE.RmService;
                bool critical = info.ApplicationType == RM_APP_TYPE.RmCritical
                                || isService           // services are stopped, never killed
                                || !snapshot.Accessible
                                || snapshot.IsCritical
                                || CriticalProcessGuard.IsCritical(pid, snapshot.ExeName);

                list.Add(new LockingProcess(
                    pid,
                    DisplayName(info.strAppName, snapshot.ExeName),
                    critical,
                    FromFileTime(info.Process.ProcessStartTime),
                    isService && !string.IsNullOrEmpty(info.strServiceShortName) ? info.strServiceShortName : null));
            }
            return list;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    /// <summary>Restart Manager gives a friendly name; append the exe so it is unambiguous.</summary>
    private static string DisplayName(string? appName, string exeName)
    {
        if (string.IsNullOrEmpty(appName)) return exeName.Length > 0 ? exeName + ".exe" : "(unknown)";
        if (exeName.Length == 0 || appName.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return appName;
        return $"{appName} [{exeName}.exe]";
    }

    private static DateTime? FromFileTime(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        long ticks = ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        return ticks == 0 ? null : DateTime.FromFileTimeUtc(ticks);
    }
}
