using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace UnityMCP.Editor
{
    // Registry records now contain a capability to execute C#. Protect the directory and the
    // temporary file BEFORE writing that capability, including when upgrading an existing registry.
    // Kept independent of Unity APIs so the actual filesystem behavior can be tested on each OS.
    internal static class PrivateRegistryFile
    {
        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        public static void WriteAllText(string path, string contents)
        {
            path = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || Directory.GetParent(directory) == null)
                throw new IOException("The UnityMCP registry must be a dedicated directory, not a filesystem root.");

            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The UnityMCP registry directory must not be a symbolic link or junction.");

            ProtectDirectory(directory);

            // CreateNew and an unpredictable name prevent following a pre-existing temporary file
            // or symlink. The protected directory prevents other users from opening this empty file
            // before its own permissions are restricted.
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ProtectFile(stream);
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        writer.Write(contents);
                }

                // Move the private file into place rather than overwriting an old record (or
                // using File.Replace, which preserves the destination's old ACL on Windows).
                // Discovery already tolerates the brief gap between deleting and moving.
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void ProtectDirectory(string directory)
        {
            if (IsWindows)
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(CurrentUserSid(), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(directory).SetAccessControl(security);
            }
            else if (Chmod(directory, Convert.ToUInt32("700", 8)) != 0)
            {
                throw PermissionError("directory");
            }
        }

        private static void ProtectFile(FileStream stream)
        {
            if (IsWindows)
            {
                var security = new FileSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(CurrentUserSid(),
                    FileSystemRights.FullControl, AccessControlType.Allow));
                // A FileAccess.Write handle does not include WRITE_DAC. Set the ACL by name
                // while our exclusive handle and the private directory prevent replacement.
                new FileInfo(stream.Name).SetAccessControl(security);
            }
            else if (Fchmod(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), Convert.ToUInt32("600", 8)) != 0)
            {
                throw PermissionError("file");
            }
        }

        private static IOException PermissionError(string target)
        {
            int error = Marshal.GetLastWin32Error();
            return new IOException("Could not restrict UnityMCP registry " + target + " permissions.",
                new Win32Exception(error));
        }

        private static SecurityIdentifier CurrentUserSid()
        {
            // Unity's Mono leaves WindowsIdentity.User and arbitrary NTAccount translation
            // unimplemented. Read TokenUser directly from the current Windows access token.
            using (var identity = WindowsIdentity.GetCurrent())
            {
                int length;
                GetTokenInformation(identity.Token, 1 /* TokenUser */, IntPtr.Zero, 0, out length);
                if (length <= 0) throw PermissionError("identity");
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(identity.Token, 1, buffer, length, out length))
                        throw PermissionError("identity");
                    // TOKEN_USER starts with SID_AND_ATTRIBUTES; its first field is the SID pointer.
                    return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr token, int informationClass,
            IntPtr information, int informationLength, out int returnLength);

        // Unity's .NET Standard profile predates File.SetUnixFileMode. chmod/fchmod are available
        // in both Linux and macOS libc; fchmod operates on our open file rather than following a path.
        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int Chmod(string path, uint mode);

        [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
        private static extern int Fchmod(int fd, uint mode);
    }
}
