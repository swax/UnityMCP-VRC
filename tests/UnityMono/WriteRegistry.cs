using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using UnityMCP.Editor;

internal static class WriteRegistry
{
    private static void Main(string[] args)
    {
        string path = Path.Combine(args[0], "instance.json");
        PrivateRegistryFile.WriteAllText(path, "synthetic-before-update");

        var permissions = new FileInfo(path).GetAccessControl();
        permissions.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(permissions);

        PrivateRegistryFile.WriteAllText(path, "synthetic-after-update");
        if (File.ReadAllText(path) != "synthetic-after-update") throw new Exception("Incorrect record content.");
        // Inspect the result with Windows APIs in run.ps1. Mono's ACL reader does not faithfully
        // report AreAccessRulesProtected, even when Windows confirms the flag is set.
    }
}
