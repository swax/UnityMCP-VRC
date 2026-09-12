# Tests

Run the registry permission tests with a .NET 9 or newer SDK:

```sh
dotnet run --project tests/RegistryPermissions/RegistryPermissions.csproj
```

This links the production `PrivateRegistryFile` helper directly and exercises the real filesystem
without starting Unity. Run on Windows and Linux/macOS to cover both Windows ACLs and Unix modes.
The tests cover new records, upgrades from permissive directories/files, temporary-file cleanup on
failure, and rejection of filesystem roots and Unix directory symlinks. Only synthetic tokens are used.

The helper also needs to compile with the plugin under Unity's supported .NET Framework and .NET
Standard profiles; the standalone .NET test executable does not replace that compatibility check.

On Windows, exercise the writer with Unity's actual Mono runtime, then independently inspect the
resulting ACLs with Windows APIs:

```powershell
./tests/UnityMono/run.ps1 -UnityEditorData 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Data'
```

This catches Mono-specific identity/ACL API limitations that modern .NET tests cannot detect.
