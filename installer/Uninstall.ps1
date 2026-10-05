[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Uninstallation requires an elevated PowerShell window so Altium registration can be updated.'
}
if (Get-Process X2 -ErrorAction SilentlyContinue) { throw 'Close Altium Designer before uninstalling EasyEDA Loader.' }

$registry = Get-ChildItem -Path (Join-Path $env:ProgramData 'Altium') -Filter ExtensionsRegistry.xml -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -ne $registry) {
    $backupRoot = Join-Path $env:ProgramData 'AltiumLcscBridge\Backups'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    Copy-Item -LiteralPath $registry.FullName -Destination (Join-Path $backupRoot ("ExtensionsRegistry-uninstall-{0}.xml" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))) -Force
    [xml]$xml = Get-Content -LiteralPath $registry.FullName -Raw
    foreach ($hrid in @('EasyEDA-Loader','Altium LCSC Bridge')) {
        $item = $xml.Extensions.SelectSingleNode("Item[@HRID='$hrid']")
        if ($null -ne $item) { [void]$xml.Extensions.RemoveChild($item) }
    }
    $temp = $registry.FullName + '.easyeda-loader.tmp'
    $settings = [Xml.XmlWriterSettings]::new(); $settings.Indent = $true; $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $writer = [Xml.XmlWriter]::Create($temp, $settings)
    try { $xml.Save($writer) } finally { $writer.Dispose() }
    Move-Item -LiteralPath $temp -Destination $registry.FullName -Force
    foreach ($folder in @('EasyEDA-Loader','Altium LCSC Bridge')) {
        $path = Join-Path (Split-Path -Parent $registry.FullName) $folder
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}

$legacyShortcut = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Altium LCSC Bridge.lnk'
if (Test-Path -LiteralPath $legacyShortcut) { Remove-Item -LiteralPath $legacyShortcut -Force }
$legacyCompanion = Join-Path $env:LOCALAPPDATA 'Programs\AltiumLcscBridge'
if (Test-Path -LiteralPath $legacyCompanion) { Remove-Item -LiteralPath $legacyCompanion -Recurse -Force }

Write-Host 'EasyEDA Loader was removed. Component libraries, settings, logs, operation journals, and browser profiles were preserved.'
