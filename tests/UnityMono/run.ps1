param([Parameter(Mandatory = $true)][string]$UnityEditorData)

$ErrorActionPreference = 'Stop'
$mono = Join-Path $UnityEditorData 'MonoBleedingEdge\bin\mono.exe'
$compiler = Join-Path $UnityEditorData 'MonoBleedingEdge\lib\mono\4.5\csc.exe'
$helper = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\UnityMCPPlugin\Editor\PrivateRegistryFile.cs'))
$testSource = Join-Path $PSScriptRoot 'WriteRegistry.cs'
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$registryTestRoot = Join-Path $tempParent ('UnityMCP-Mono-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $registryTestRoot | Out-Null

try {
    $executable = Join-Path $registryTestRoot 'WriteRegistry.exe'
    & $mono $compiler -nologo "-out:$executable" $helper $testSource
    if ($LASTEXITCODE -ne 0) { throw 'Unity Mono compilation failed.' }
    $registry = Join-Path $registryTestRoot 'registry'
    & $mono $executable $registry
    if ($LASTEXITCODE -ne 0) { throw 'Unity Mono registry writer failed.' }

    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($path in @($registry, (Join-Path $registry 'instance.json'))) {
        $acl = Get-Acl -LiteralPath $path
        if (-not $acl.AreAccessRulesProtected -or $acl.Access.Count -ne 1) {
            throw 'Registry ACL is not protected and restricted to one user.'
        }
        $rule = $acl.Access[0]
        if ($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]) -ne $currentUser -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl) {
            throw 'Registry ACL does not grant full control exclusively to the current user.'
        }
    }
    Write-Output 'PASS: Unity Mono creates and replaces records with protected user-only Windows ACLs.'
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($registryTestRoot)
    if (-not $resolvedTestRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolvedTestRoot).StartsWith('UnityMCP-Mono-test-')) {
        throw 'Refusing to remove a directory outside the test temporary location.'
    }
    Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
}
