using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using UnityMCP.Editor;

// Exercises the production writer on real files, with no Unity installation or NuGet dependencies.
internal static class Program
{
    private static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "UnityMCP-permission-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "new-registry", "instance.json");
            PrivateRegistryFile.WriteAllText(path, "synthetic-token-one");
            Check(File.ReadAllText(path) == "synthetic-token-one", "new record content");
            CheckPrivate(path);
            Check(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "no leftover temporary file");
            Console.WriteLine("PASS: new registry and record are private");

            // Simulate the old writer's permissive directory and file, including explicit ACLs
            // which cannot be fixed just by disabling inherited permissions on Windows.
            MakePublic(Path.GetDirectoryName(path), path);
            PrivateRegistryFile.WriteAllText(path, "synthetic-token-two");
            Check(File.ReadAllText(path) == "synthetic-token-two", "updated record content");
            CheckPrivate(path);
            Check(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "replacement temporary cleanup");
            Console.WriteLine("PASS: permissive existing directory and record are repaired");

            string blocked = Path.Combine(root, "blocked-registry", "instance.json");
            Directory.CreateDirectory(blocked); // A directory in place of the destination file.
            ExpectIOException(() => PrivateRegistryFile.WriteAllText(blocked, "must-not-be-published"));
            Check(Directory.GetFiles(Path.GetDirectoryName(blocked)).Length == 0, "failed write leaves no secret temporary file");
            Console.WriteLine("PASS: failed publication removes the temporary token file");

            if (!IsWindows)
            {
                string target = Path.Combine(root, "symlink-target");
                string link = Path.Combine(root, "linked-registry");
                Directory.CreateDirectory(target);
                Directory.CreateSymbolicLink(link, target);
                ExpectIOException(() => PrivateRegistryFile.WriteAllText(Path.Combine(link, "instance.json"), "must-not-be-written"));
                Check(Directory.GetFiles(target).Length == 0, "symlink target remains empty");
                Directory.Delete(link);
                Console.WriteLine("PASS: registry directory symlinks are rejected");
            }

            ExpectIOException(() => PrivateRegistryFile.WriteAllText(Path.Combine(Path.GetPathRoot(root), "instance.json"), "must-not-be-written"));
            Console.WriteLine("PASS: filesystem roots are rejected before any permission change");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            // Only remove the uniquely named directory this test created under the OS temp folder.
            string allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(allowed, StringComparison.Ordinal) &&
                Path.GetFileName(root).StartsWith("UnityMCP-permission-test-", StringComparison.Ordinal))
                Directory.Delete(root, true);
        }
    }

    private static void CheckPrivate(string path)
    {
        string directory = Path.GetDirectoryName(path);
        if (IsWindows)
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                CheckAcl(new DirectoryInfo(directory).GetAccessControl(), identity.User);
                CheckAcl(new FileInfo(path).GetAccessControl(), identity.User);
            }
        }
        else
        {
            Check((int)File.GetUnixFileMode(directory) == Convert.ToInt32("700", 8), "directory mode must be 0700");
            Check((int)File.GetUnixFileMode(path) == Convert.ToInt32("600", 8), "record mode must be 0600");
        }
    }

    private static void CheckAcl(FileSystemSecurity security, SecurityIdentifier user)
    {
        Check(security.AreAccessRulesProtected, "ACL inheritance disabled");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Check(rules.Length == 1 && rules[0].IdentityReference.Equals(user) &&
            rules[0].AccessControlType == AccessControlType.Allow && rules[0].FileSystemRights == FileSystemRights.FullControl,
            "only the current user has an allow rule");
    }

    private static void MakePublic(string directory, string path)
    {
        if (IsWindows)
        {
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var directorySecurity = new DirectoryInfo(directory).GetAccessControl();
            directorySecurity.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(directorySecurity);
            var fileSecurity = new FileInfo(path).GetAccessControl();
            fileSecurity.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(fileSecurity);
        }
        else
        {
            File.SetUnixFileMode(directory, (UnixFileMode)Convert.ToInt32("755", 8));
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("644", 8));
        }
    }

    private static void ExpectIOException(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new Exception("Expected an IOException");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("Failed: " + message);
    }
}
