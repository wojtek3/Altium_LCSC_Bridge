[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\AltiumLcscBridge'
$scriptsRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'AltiumScripts\LcscBridge'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Altium LCSC Bridge.lnk'

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Uninstallation requires an elevated PowerShell window so the Altium extension registration can be removed.'
}
if (Get-Process X2 -ErrorAction SilentlyContinue) { throw 'Close Altium Designer before uninstalling the bridge.' }

Get-Process AltiumLcscBridge -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath -Force }
if (Test-Path -LiteralPath $scriptsRoot) { Remove-Item -LiteralPath $scriptsRoot -Recurse -Force }

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
    $registryTemp = $registry.FullName + '.lcsc-bridge.tmp'
    $writerSettings = [Xml.XmlWriterSettings]::new()
    $writerSettings.Indent = $true
    $writerSettings.IndentChars = '  '
    $writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
    $writer = [Xml.XmlWriter]::Create($registryTemp, $writerSettings)
    try { $xml.Save($writer) } finally { $writer.Dispose() }
    Move-Item -LiteralPath $registryTemp -Destination $registry.FullName -Force
    foreach ($folderName in @('EasyEDA-Loader','Altium LCSC Bridge')) {
        $adapterRoot = Join-Path (Split-Path -Parent $registry.FullName) $folderName
        if (Test-Path -LiteralPath $adapterRoot) { Remove-Item -LiteralPath $adapterRoot -Recurse -Force }
    }
}

if ((Resolve-Path -LiteralPath $installRoot -ErrorAction SilentlyContinue).Path -eq $installRoot) {
    $cleanup = "Start-Sleep -Milliseconds 700; Remove-Item -LiteralPath '$($installRoot.Replace("'", "''"))' -Recurse -Force"
    Start-Process powershell.exe -ArgumentList '-NoProfile','-WindowStyle','Hidden','-Command',$cleanup -WindowStyle Hidden
}
Write-Host 'Altium LCSC Bridge was removed. Documents\LCSC and its cached libraries were preserved.'
