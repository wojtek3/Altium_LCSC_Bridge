[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$AltiumInstallDir = 'C:\Program Files\Altium\AD23'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }

function Invoke-DotNetCommand {
    param([Parameter(Mandatory)][string[]]$DotNetArguments)
    & $dotnet @DotNetArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($DotNetArguments[0]) failed with exit code $LASTEXITCODE." }
}

$solution = Join-Path $repoRoot 'AltiumLcscBridge.slnx'
$altiumProperty = "-p:AltiumInstallDir=$AltiumInstallDir"
Invoke-DotNetCommand @('restore', $solution, $altiumProperty)
Invoke-DotNetCommand @('build', $solution, '-c', $Configuration, '--no-restore', $altiumProperty)
Invoke-DotNetCommand @('run', '--project', (Join-Path $repoRoot 'tests\LcscBridge.Tests\LcscBridge.Tests.csproj'), '-c', $Configuration, '--no-build')

$publish = Join-Path $repoRoot 'artifacts\publish-0.3.0'
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
$extension = Join-Path $publish 'altium-extension'
New-Item -ItemType Directory -Path $extension -Force | Out-Null

$adapterOutput = Join-Path $repoRoot "src\LcscBridge.AltiumAdapter\bin\$Configuration"
$required = @(
    'EasyEDA-Loader.dll','EasyEDA-Loader.dll.config','EasyEDA-Loader.deps.json','EasyEDA-Loader.Ins','EasyEDA-Loader.rcs',
    'EasyEDA.Loader.UI.dll','LcscBridge.Core.dll','Microsoft.Data.Sqlite.dll','Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.Wpf.dll','Newtonsoft.Json.dll','SQLitePCLRaw.batteries_v2.dll','SQLitePCLRaw.core.dll',
    'SQLitePCLRaw.provider.e_sqlite3.dll'
)
foreach ($name in $required) {
    $path = Join-Path $adapterOutput $name
    if (-not (Test-Path -LiteralPath $path)) { throw "Required extension output is missing: $path" }
    Copy-Item -LiteralPath $path -Destination $extension -Force
}
$nativeSource = Join-Path $adapterOutput 'runtimes\win-x64\native'
if (-not (Test-Path -LiteralPath (Join-Path $nativeSource 'WebView2Loader.dll')) -or
    -not (Test-Path -LiteralPath (Join-Path $nativeSource 'e_sqlite3.dll'))) {
    throw 'The x64 WebView2 or SQLite native dependency is missing.'
}
$nativeDestination = Join-Path $extension 'runtimes\win-x64\native'
New-Item -ItemType Directory -Path $nativeDestination -Force | Out-Null
Copy-Item -Path (Join-Path $nativeSource '*') -Destination $nativeDestination -Recurse -Force

Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination (Join-Path $publish 'docs') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\LcscBridge.AltiumAdapter\LICENSE') -Destination (Join-Path $publish 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\Install.ps1') -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\Uninstall.ps1') -Destination $publish -Force

$sourceRoot = Join-Path $publish 'source'
New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
foreach ($sourceName in @('LcscBridge.AltiumAdapter','LcscBridge.Core','LcscBridge.App','Shared')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "src\$sourceName") -Destination (Join-Path $sourceRoot $sourceName) -Recurse -Force
}
foreach ($generated in Get-ChildItem -LiteralPath $sourceRoot -Directory -Recurse | Where-Object Name -In @('bin','obj')) {
    Remove-Item -LiteralPath $generated.FullName -Recurse -Force
}

$archive = Join-Path $repoRoot 'artifacts\EasyEDA-Loader-0.3.0-win-x64.zip'
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -Force
Write-Host "Unified extension release created at $publish"
Write-Host "Installer archive created at $archive"
