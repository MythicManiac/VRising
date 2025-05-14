using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ServerLaunchFix
{
    /// <summary>
    /// Creates, deletes, checks, and retrieves target info for Windows/Wine-style symlinks via kernel32.dll.
    /// </summary>
    public static class Symlink
    {
        /// <summary>Flag for CreateSymbolicLink indicating a file symlink.</summary>
        private const int SYMBOLIC_LINK_FLAG_FILE = 0x0;
        /// <summary>Flag for CreateSymbolicLink indicating a directory symlink.</summary>
        private const int SYMBOLIC_LINK_FLAG_DIRECTORY = 0x1;

        /// <summary> Used in DeviceIoControl for retrieving the reparse data. </summary>
        private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const uint FSCTL_DELETE_REPARSE_POINT = 0x000900AC;

        /// <summary> Reparse point tag for actual symlinks. </summary>
        private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

        private const string NonInterpretedPathPrefix = @"\??\";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLink(
            string lpSymlinkFileName,
            string lpTargetFileName,
            int dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            EFileAccess dwDesiredAccess,
            EFileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            ECreationDisposition dwCreationDisposition,
            EFileAttributes dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        /// <summary>
        /// Create a symlink at linkPath pointing to targetPath using the Windows/Wine API.
        /// </summary>
        /// <param name="linkPath">Where you want the symlink to live.</param>
        /// <param name="targetPath">What the symlink points to.</param>
        /// <param name="overwrite">If true, any existing item at linkPath is deleted first.</param>
        public static void Create(string linkPath, string targetPath, bool overwrite)
        {
            linkPath = Path.GetFullPath(linkPath);
            targetPath = Path.GetFullPath(targetPath);

            bool isDirTarget = Directory.Exists(targetPath);

            // Overwrite as requested:
            if (File.Exists(linkPath) || Directory.Exists(linkPath))
            {
                if (!overwrite)
                    throw new IOException($"Path '{linkPath}' already exists and overwrite = false.");
                Delete(linkPath);
            }
            else
            {
                // Ensure parent directory of linkPath exists
                string parent = Path.GetDirectoryName(linkPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);
            }

            // Actually create the symlink
            bool success = CreateSymbolicLink(
                lpSymlinkFileName: linkPath,
                lpTargetFileName: targetPath,
                dwFlags: isDirTarget ? SYMBOLIC_LINK_FLAG_DIRECTORY : SYMBOLIC_LINK_FLAG_FILE);

            if (!success)
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"CreateSymbolicLink failed (err {err}).");
            }
        }

        /// <summary>
        /// Deletes a symlink at the given path.
        /// </summary>
        public static void Delete(string linkPath)
        {
            linkPath = Path.GetFullPath(linkPath);

            if (Directory.Exists(linkPath))
            {
                // For a Windows symlink to a directory, Directory.Delete usually just removes the link
                Directory.Delete(linkPath);
            }
            else if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }
        }

        /// <summary>
        /// Checks whether the path exists and is in fact a Windows/Wine symlink.
        /// </summary>
        public static bool Exists(string path)
        {
            path = Path.GetFullPath(path);

            if (!File.Exists(path) && !Directory.Exists(path))
                return false;

            // Attempt to retrieve symlink info via DeviceIoControl
            using (var handle = OpenReparsePoint(path, EFileAccess.GenericRead))
            {
                var data = InternalGetReparseData(handle);
                if (data == null)
                    return false;
                return (data.Value.ReparseTag == IO_REPARSE_TAG_SYMLINK);
            }
        }

        /// <summary>
        /// If path is a symlink, returns the target path. Otherwise throws an IOException.
        /// </summary>
        public static string GetTarget(string linkPath)
        {
            linkPath = Path.GetFullPath(linkPath);
            if (!Exists(linkPath))
                throw new IOException($"Path '{linkPath}' is not a symlink.");

            using (var handle = OpenReparsePoint(linkPath, EFileAccess.GenericRead))
            {
                var data = InternalGetReparseData(handle);
                if (data == null || data.Value.ReparseTag != IO_REPARSE_TAG_SYMLINK)
                    throw new IOException("Path is not a valid symlink.");

                // Convert from reparse data to actual string:
                var tagData = data.Value;
                var target = Encoding.Unicode.GetString(
                    tagData.PathBuffer,
                    tagData.SubstituteNameOffset,
                    tagData.SubstituteNameLength);

                // Trim the "\??\" prefix if present
                if (target.StartsWith(NonInterpretedPathPrefix))
                    target = target.Substring(NonInterpretedPathPrefix.Length);

                return target;
            }
        }

        #region Low-level reparse point helpers

        [StructLayout(LayoutKind.Sequential)]
        private struct REPARSE_DATA_BUFFER
        {
            public uint ReparseTag;
            public ushort ReparseDataLength;
            public ushort Reserved;
            public ushort SubstituteNameOffset;
            public ushort SubstituteNameLength;
            public ushort PrintNameOffset;
            public ushort PrintNameLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x3FF0)]
            public byte[] PathBuffer;
        }

        private static SafeFileHandle OpenReparsePoint(string reparsePoint, EFileAccess accessMode)
        {
            IntPtr handle = CreateFile(
                reparsePoint,
                accessMode,
                EFileShare.Read | EFileShare.Write | EFileShare.Delete,
                IntPtr.Zero,
                ECreationDisposition.OpenExisting,
                EFileAttributes.BackupSemantics | EFileAttributes.OpenReparsePoint,
                IntPtr.Zero);

            // Wrap in SafeFileHandle:
            var safeHandle = new SafeFileHandle(handle, true);
            if (safeHandle.IsInvalid)
                ThrowLastWin32Error("Unable to open reparse point.");

            return safeHandle;
        }

        private static REPARSE_DATA_BUFFER? InternalGetReparseData(SafeFileHandle handle)
        {
            var outBufferSize = Marshal.SizeOf(typeof(REPARSE_DATA_BUFFER));
            IntPtr outBuffer = Marshal.AllocHGlobal(outBufferSize);
            try
            {
                bool result = DeviceIoControl(
                    handle.DangerousGetHandle(),
                    FSCTL_GET_REPARSE_POINT,
                    IntPtr.Zero,
                    0,
                    outBuffer,
                    outBufferSize,
                    out int bytesReturned,
                    IntPtr.Zero);

                if (!result)
                {
                    int error = Marshal.GetLastWin32Error();
                    // ERROR_NOT_A_REPARSE_POINT = 4390 => not a link.
                    // In that case, we just return null.
                    if (error == 4390) 
                        return null;

                    ThrowLastWin32Error("Failed to read reparse data");
                }

                var data = Marshal.PtrToStructure<REPARSE_DATA_BUFFER>(outBuffer);
                return data;
            }
            finally
            {
                Marshal.FreeHGlobal(outBuffer);
            }
        }

        private static void ThrowLastWin32Error(string message)
        {
            throw new IOException(
                message,
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        #endregion

        #region Enums

        [Flags]
        private enum EFileAccess : uint
        {
            GenericRead = 0x80000000,
            GenericWrite = 0x40000000,
            GenericExecute = 0x20000000,
            GenericAll = 0x10000000
        }

        [Flags]
        private enum EFileShare : uint
        {
            None = 0x00000000,
            Read = 0x00000001,
            Write = 0x00000002,
            Delete = 0x00000004
        }

        private enum ECreationDisposition : uint
        {
            New = 1,
            CreateAlways = 2,
            OpenExisting = 3,
            OpenAlways = 4,
            TruncateExisting = 5
        }

        [Flags]
        private enum EFileAttributes : uint
        {
            Readonly = 0x00000001,
            Hidden = 0x00000002,
            System = 0x00000004,
            Directory = 0x00000010,
            Archive = 0x00000020,
            Device = 0x00000040,
            Normal = 0x00000080,
            Temporary = 0x00000100,
            SparseFile = 0x00000200,
            ReparsePoint = 0x00000400,
            Compressed = 0x00000800,
            Offline = 0x00001000,
            NotContentIndexed = 0x00002000,
            Encrypted = 0x00004000,
            Write_Through = 0x80000000,
            Overlapped = 0x40000000,
            NoBuffering = 0x20000000,
            RandomAccess = 0x10000000,
            SequentialScan = 0x08000000,
            DeleteOnClose = 0x04000000,
            BackupSemantics = 0x02000000,
            PosixSemantics = 0x01000000,
            OpenReparsePoint = 0x00200000,
            OpenNoRecall = 0x00100000,
            FirstPipeInstance = 0x00080000
        }

        #endregion
    }
}