using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentSpeed.Core.IO;

/// <summary>Identifies one physical file: Windows volume/index, Linux device/inode, or macOS creation time.</summary>
internal readonly record struct FileIdentity(long CreationTicks, ulong Volume, ulong FileIndex)
{
    /// <summary>Null where no stable identity is available (creation time is not reliable on every platform).</summary>
    public static FileIdentity? From(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            return NativeMethods.GetFileInformationByHandle(handle, out var info)
                ? new FileIdentity(
                    ((long)info.CreationTimeHigh << 32) | info.CreationTimeLow,
                    info.VolumeSerialNumber,
                    ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
                : null;
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            return new FileIdentity(File.GetCreationTimeUtc(handle).Ticks, 0, 0);
        }

        if (OperatingSystem.IsLinux())
        {
            // statx has the same fixed ABI on x64 and arm64. AT_EMPTY_PATH identifies the already-open file,
            // avoiding a path lookup racing with rename, and unlike ctime the identity survives appends.
            if (NativeMethods.Statx(handle, "", 0x1000, 0x100, out var info) == 0 && (info.Mask & 0x100) != 0)
            {
                return new FileIdentity(0, ((ulong)info.DeviceMajor << 32) | info.DeviceMinor, info.Inode);
            }
        }

        return null;
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [SupportedOSPlatform("linux")]
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Statx(SafeFileHandle directory, string path, int flags, uint mask, out LinuxStatx information);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation information);
}
