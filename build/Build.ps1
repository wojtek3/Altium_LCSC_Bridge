[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }

function Invoke-DotNetCommand {
    param([Parameter(Mandatory)][string[]]$DotNetArguments)
    & $dotnet @DotNetArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($DotNetArguments[0]) failed with exit code $LASTEXITCODE."
    }
}

Invoke-DotNetCommand @('restore', (Join-Path $repoRoot 'AltiumLcscBridge.slnx'))
Invoke-DotNetCommand @('build', (Join-Path $repoRoot 'AltiumLcscBridge.slnx'), '-c', $Configuration, '--no-restore')
Invoke-DotNetCommand @('run', '--project', (Join-Path $repoRoot 'tests\LcscBridge.Tests\LcscBridge.Tests.csproj'), '-c', $Configuration, '--no-build')

$publish = Join-Path $repoRoot 'artifacts\publish-0.2.4'
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
Invoke-DotNetCommand @('publish', (Join-Path $repoRoot 'src\LcscBridge.App\LcscBridge.App.csproj'), '-c', $Configuration,
    '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-o', $publish)
Copy-Item -LiteralPath (Join-Path $repoRoot 'altium') -Destination (Join-Path $publish 'altium') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination (Join-Path $publish 'docs') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\Install.ps1') -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\Uninstall.ps1') -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $publish -Force

$adapterOutput = Join-Path $repoRoot "src\LcscBridge.AltiumAdapter\bin\$Configuration"
$adapterPublish = Join-Path $publish 'altium-extension'
New-Item -ItemType Directory -Path $adapterPublish -Force | Out-Null
foreach ($name in @('EasyEDA-Loader.dll','EasyEDA-Loader.dll.config','EasyEDA-Loader.deps.json','EasyEDA-Loader.Ins','EasyEDA-Loader.rcs','Newtonsoft.Json.dll')) {
    $adapterFile = Join-Path $adapterOutput $name
    if (-not (Test-Path -LiteralPath $adapterFile)) { throw "Required Altium adapter output is missing: $adapterFile" }
    Copy-Item -LiteralPath $adapterFile -Destination $adapterPublish -Force
}
$sourcePublish = Join-Path $publish 'source\LcscBridge.AltiumAdapter'
New-Item -ItemType Directory -Path (Split-Path -Parent $sourcePublish) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\LcscBridge.AltiumAdapter') -Destination $sourcePublish -Recurse -Force
$sharedSourcePublish = Join-Path $publish 'source\Shared'
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\Shared') -Destination $sharedSourcePublish -Recurse -Force
foreach ($projectName in @('LcscBridge.Core','LcscBridge.App')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "src\$projectName") -Destination (Join-Path $publish "source\$projectName") -Recurse -Force
}
foreach ($sourceProject in Get-ChildItem -LiteralPath (Join-Path $publish 'source') -Directory) {
    foreach ($generated in @('bin','obj')) {
        $generatedPath = Join-Path $sourceProject.FullName $generated
        if (Test-Path -LiteralPath $generatedPath) { Remove-Item -LiteralPath $generatedPath -Recurse -Force }
    }
}

Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $publish 'altium\lcsc-bridge.bmp'
$bitmap = [System.Drawing.Bitmap]::new(18, 18)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.Clear([System.Drawing.Color]::White)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $blue = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(23, 105, 170))
    $white = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 2)
    try {
        $graphics.FillEllipse($blue, 1, 1, 16, 16)
        $graphics.DrawLine($white, 6, 5, 6, 13)
        $graphics.DrawLine($white, 6, 13, 12, 13)
        $graphics.DrawLine($white, 11, 5, 11, 10)
    } finally { $blue.Dispose(); $white.Dispose() }
    $bitmap.Save($iconPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
} finally { $graphics.Dispose(); $bitmap.Dispose() }
$archive = Join-Path $repoRoot 'artifacts\AltiumLcscBridge-win-x64.zip'
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -Force
Write-Host "Release created at $publish"
Write-Host "Installer archive created at $archive"
