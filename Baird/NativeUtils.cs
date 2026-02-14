using System.Runtime.InteropServices;

namespace Baird;

public static class NativeUtils
{
    [DllImport("/System/Library/Frameworks/Carbon.framework/Versions/Current/Carbon")]
    private static extern IntPtr CGEventSourceCreate(int stateID);

    [DllImport("/System/Library/Frameworks/Carbon.framework/Versions/Current/Carbon")]
    private static extern bool CGEventSourceKeyState(int stateID, ushort key);

    // kCGEventSourceStateHIDSystemState = 1
    private const int kCGEventSourceStateHIDSystemState = 1;

    // kVK_CapsLock = 0x39 (57)
    private const ushort kVK_CapsLock = 0x39;

    public static bool GetCapsLockState()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return CGEventSourceKeyState(kCGEventSourceStateHIDSystemState, kVK_CapsLock);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // This is only supported on Windows
                return System.Console.CapsLock;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }
}
