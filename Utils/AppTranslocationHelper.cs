using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LuckyLilliaDesktop.Utils;

[SupportedOSPlatform("macos")]
public static class AppTranslocationHelper
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int kCFStringEncodingUTF8 = 0x08000100;
    private const int kCFURLPOSIXPathStyle = 0;

    [DllImport(SecurityFramework)]
    private static extern IntPtr SecTranslocateCreateOriginalPathForURL(IntPtr translocatedUrl, out IntPtr error);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFURLCreateWithFileSystemPath(IntPtr allocator, IntPtr filePath, int pathStyle, bool isDirectory);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFURLCopyFileSystemPath(IntPtr url, int pathStyle);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, int encoding);

    [DllImport(CoreFoundationFramework)]
    private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, int bufferSize, int encoding);

    [DllImport(CoreFoundationFramework)]
    private static extern int CFStringGetLength(IntPtr theString);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr obj);

    /// <summary>
    /// 获取 App Translocation 前的原始路径
    /// </summary>
    public static string? GetOriginalPath(string translocatedAppPath)
    {
        IntPtr cfPath = IntPtr.Zero;
        IntPtr cfUrl = IntPtr.Zero;
        IntPtr originalUrl = IntPtr.Zero;
        IntPtr originalCfPath = IntPtr.Zero;
        IntPtr error = IntPtr.Zero;

        try
        {
            cfPath = CFStringCreateWithCString(IntPtr.Zero, translocatedAppPath, kCFStringEncodingUTF8);
            if (cfPath == IntPtr.Zero) return null;

            cfUrl = CFURLCreateWithFileSystemPath(IntPtr.Zero, cfPath, kCFURLPOSIXPathStyle, true);
            if (cfUrl == IntPtr.Zero) return null;

            originalUrl = SecTranslocateCreateOriginalPathForURL(cfUrl, out error);
            if (originalUrl == IntPtr.Zero) return null;

            originalCfPath = CFURLCopyFileSystemPath(originalUrl, kCFURLPOSIXPathStyle);
            if (originalCfPath == IntPtr.Zero) return null;

            var length = CFStringGetLength(originalCfPath);
            var buffer = new byte[length * 4 + 1];
            if (CFStringGetCString(originalCfPath, buffer, buffer.Length, kCFStringEncodingUTF8))
            {
                var nullIndex = Array.IndexOf(buffer, (byte)0);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, nullIndex >= 0 ? nullIndex : buffer.Length);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (originalCfPath != IntPtr.Zero) CFRelease(originalCfPath);
            if (originalUrl != IntPtr.Zero) CFRelease(originalUrl);
            if (cfUrl != IntPtr.Zero) CFRelease(cfUrl);
            if (cfPath != IntPtr.Zero) CFRelease(cfPath);
            if (error != IntPtr.Zero) CFRelease(error);
        }
    }
}
