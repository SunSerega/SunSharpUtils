
using System;
using System.IO;
using System.Runtime.InteropServices;

// https://learn.microsoft.com/en-us/windows/win32/api/_rstmgr/
// https://github.com/seproDev/yt-dlp-ChromeCookieUnlock/blob/main/yt_dlp_plugins/postprocessor/chrome_cookie_unlock.py
// https://stackoverflow.com/questions/317071/how-do-i-find-out-which-process-is-locking-a-file-using-net

// I lost the original motivation for this class, but it can be generally useful
// - The Report function is a meme as is, should be adapted (rewritten) to next use case

namespace SunSharpUtils.RestartManager;

using static RestartManagerApi;

#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable SYSLIB1054 // Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time
#pragma warning restore IDE0079 // Remove unnecessary suppression

/// <summary>
/// Uses WinAPI to unlock files used by other processes
/// </summary>
public static class RestartManager
#pragma warning restore IDE0079 // Remove unnecessary suppression
{

    /// <summary>
    /// Invoked when reporting status messages
    /// </summary>
    public static event Action<String>? OnReport;
    private static void Report(String message) => OnReport?.Invoke(message);

    /// <summary>
    /// Tries to unlock the path until the use action succeeds
    /// </summary>
    /// <param name="path"></param>
    /// <param name="use"></param>
    public static void UnlockAndUse(String path, Action use)
    {
        var try_i = 0;
        while (true)
        {
            try
            {
                try_i += 1;
                use.Invoke();
                break;
            }
            catch (Exception ex)
            {
                if (try_i % 1000 == 0)
                    Err.Handle(ex);
                if (ex is not IOException || ex.Message != $"The process cannot access the file '{path}' because it is being used by another process.")
                    Report($"Unexpected error: {ex}");
                Unlock(path);
            }
        }
    }

    private static void Unlock(String path)
    {
        try
        {
            RmStartSession_Wrap(out var session_handle, RmStartSessionFlags.None, out var sessionKey).ThrowIfFailed();
            Report($"Session Handle: {session_handle.Value}, Session Key: {sessionKey}");
            try
            {
                RmRegisterResources_WrapFiles(session_handle, path).ThrowIfFailed();
                Report("Resources registered successfully");

                if (GetAndPrintHeldApps() == 0)
                {
                    Report("No applications are holding the file");
                    return;
                }

                var shutdown_result = RmShutdown(session_handle, RmShutdownFlags.Force, percent_complete => Report($"Unlocking: {percent_complete}%"));
                shutdown_result.ThrowIfFailed();

                Int32 GetAndPrintHeldApps()
                {
                    RmGetList_Wrap(session_handle, out var apps, out var rebootReasons).ThrowIfFailed();
                    Report($"Reboot Reasons: {rebootReasons}");
                    Report($"Affected Applications: {apps.Length}");
                    foreach (var app in apps)
                        Report($"- App Name: {app.strAppName}, PID: {app.Process.dwProcessId}, Status: {app.AppStatus}");
                    return apps.Length;
                }
            }
            finally
            {
                RmEndSession(session_handle).ThrowIfFailed();
                Report("Session ended");
            }
        }
        catch (Exception ex)
        {
            Err.Handle(ex);
        }
    }

}

internal static class RestartManagerApi
{

    public static RmResultCode RmStartSession_Wrap(out RmSessionHandle pSessionHandle, RmStartSessionFlags dwSessionFlags, out String strSessionKey)
    {
        const Int32 CCH_RM_SESSION_KEY = 32;
        var sessionKey_chars = new Char[CCH_RM_SESSION_KEY + 1];
        var error = RmStartSession(out pSessionHandle, dwSessionFlags, sessionKey_chars);
        strSessionKey = new String(sessionKey_chars, 0, CCH_RM_SESSION_KEY);
        return error;
    }
    [DllImport(@"rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern RmResultCode RmStartSession(out RmSessionHandle pSessionHandle, RmStartSessionFlags dwSessionFlags, Char[] strSessionKey);

    [DllImport(@"rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern RmResultCode RmEndSession(RmSessionHandle pSessionHandle);

    public static RmResultCode RmRegisterResources_WrapFiles(RmSessionHandle session_handle, params String[] files)
    {
        return RmRegisterResources(
            dwSessionHandle: session_handle,
            nFiles: (UInt32)files.Length,
            rgsFilenames: files,
            nApplications: 0,
            rgApplications: null,
            nServices: 0,
            rgsServiceNames: null
        );
    }
    [DllImport(@"rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern RmResultCode RmRegisterResources(
        RmSessionHandle dwSessionHandle,
        UInt32 nFiles,
        String[]? rgsFilenames,
        UInt32 nApplications,
        RmUniqueProcess[]? rgApplications,
        UInt32 nServices,
        String[]? rgsServiceNames
    );

    public static RmResultCode RmGetList_Wrap(RmSessionHandle session_handle, out RmProcessInfo[] affectedApps, out RmRebootReason rebootReasons)
    {

        UInt32 apps_read = 0;
        var result_code = RmGetList(session_handle, out var apps_len, ref apps_read, null, out rebootReasons);
        var expect_data = false;
        if (result_code is RmResultCode.MORE_DATA)
        {
            expect_data = true;
            result_code = RmResultCode.SUCCESS;
        }
        if (result_code.IsError())
        {
            affectedApps = [];
            return result_code;
        }
        if (expect_data != (apps_len != 0))
            throw new InvalidOperationException($"Expected data presence mismatch. Expect data: {expect_data}, Apps len: {apps_len}");

        affectedApps = new RmProcessInfo[apps_len];
        apps_read = apps_len;
        result_code = RmGetList(session_handle, out _, ref apps_read, affectedApps, out rebootReasons);
        if (apps_len != apps_read)
            throw new InvalidOperationException($"Expected {apps_len} apps, but got {apps_read}");

        return result_code;
    }
    [DllImport(@"rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern RmResultCode RmGetList(
        RmSessionHandle dwSessionHandle,
        out UInt32 pnProcInfoNeeded,
        ref UInt32 pnProcInfo,
        [Out] RmProcessInfo[]? rgAffectedApps,
        out RmRebootReason lpdwRebootReasons
    );

    [DllImport(@"rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern RmResultCode RmShutdown(
        RmSessionHandle dwSessionHandle,
        RmShutdownFlags lActionFlags,
        RmWriteStatusCallback? fnStatus
    );



    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public record struct RmSessionHandle(UInt32 Value);

    public enum RmResultCode : UInt32
    {
        SUCCESS = 0,
        ACCESS_DENIED = 5,
        INVALID_HANDLE = 6,
        OUTOFMEMORY = 14,
        WRITE_FAULT = 29,
        SEM_TIMEOUT = 121,
        BAD_ARGUMENTS = 160,
        MORE_DATA = 234,
        FAIL_NOACTION_REBOOT = 350,
        FAIL_SHUTDOWN = 351,
        MAX_SESSIONS_REACHED = 353,
        CANCELLED = 1223,
    }

    public static Boolean IsError(this RmResultCode code) => code != RmResultCode.SUCCESS;
    public static void ThrowIfFailed(this RmResultCode code)
    {
        if (!code.IsError())
            return;
        throw new InvalidOperationException($"RM operation failed with code: {code}");
    }

    public enum RmStartSessionFlags : UInt32
    {
        None = 0,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RmProcessInfo
    {
        private const Int32 CCH_RM_MAX_APP_NAME = 255;
        private const Int32 CCH_RM_MAX_SVC_NAME = 63;

        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME+1)]
        public String strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME+1)]
        public String strServiceShortName;
        public RmApplicationType ApplicationType;
        public RmAppStatus AppStatus;
        public UInt32 TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public Boolean bRestartable;

    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public readonly struct RmUniqueProcess
    {
        public readonly UInt32 dwProcessId;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        public DateTime PST => DateTime.FromFileTimeUtc(((Int64)this.ProcessStartTime.dwHighDateTime << 32) | (UInt32)this.ProcessStartTime.dwLowDateTime);
    }

    public enum RmApplicationType : UInt32
    {
        UnknownApp = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000,
    }

    [Flags]
    public enum RmAppStatus : UInt32
    {
        Unknown = 0x0,
        Running = 0x1,
        Stopped = 0x2,
        StoppedOther = 0x4,
        Restarted = 0x8,
        ErrorOnStop = 0x10,
        ErrorOnRestart = 0x20,
        ShutdownMasked = 0x40,
        RestartMasked = 0x80,
    }

    [Flags]
    public enum RmRebootReason : UInt32
    {
        None = 0,
        PermissionDenied = 0x1,
        SessionMismatch = 0x2,
        CriticalProcess = 0x4,
        CriticalService = 0x8,
        DetectedSelf = 0x10,
    }

    [Flags]
    public enum RmShutdownFlags : UInt32
    {
        Force = 0x1,
        ShutdownOnlyRegistered = 0x10,
    }

    public delegate void RmWriteStatusCallback(UInt32 nPercentComplete);

}
