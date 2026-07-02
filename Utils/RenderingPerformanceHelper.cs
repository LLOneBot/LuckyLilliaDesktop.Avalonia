using System;
using System.Runtime.InteropServices;

namespace LuckyLilliaDesktop.Utils;

public static class RenderingPerformanceHelper
{
    public static bool UseReducedMotion { get; } = IsReducedMotionRequested();

    private static bool IsReducedMotionRequested()
    {
        var env = Environment.GetEnvironmentVariable("LLD_LOW_PERF_RENDERING");
        if (env is "1" || env?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && (IsRemoteSession() || IsWindowsServer());
    }

    private static bool IsRemoteSession()
    {
        const int SM_REMOTESESSION = 0x1000;
        return GetSystemMetrics(SM_REMOTESESSION) != 0;
    }

    private static bool IsWindowsServer()
    {
        var version = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEX>() };
        const byte VER_NT_WORKSTATION = 1;
        return RtlGetVersion(ref version) == 0 && version.wProductType != VER_NT_WORKSTATION;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OSVERSIONINFOEX versionInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OSVERSIONINFOEX
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }
}
