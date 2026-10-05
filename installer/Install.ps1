[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'altium-extension')) { $source = $PSScriptRoot }
$extensionSource = Join-Path $source 'altium-extension'
if (-not (Test-Path -LiteralPath (Join-Path $extensionSource 'EasyEDA-Loader.dll')) -or
    -not (Test-Path -LiteralPath (Join-Path $extensionSource 'EasyEDA.Loader.UI.dll')) -or
    -not (Test-Path -LiteralPath (Join-Path $extensionSource 'LcscBridge.Core.dll'))) {
    throw 'The unified EasyEDA Loader extension payload is incomplete. Run build\Build.ps1 first.'
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installation requires an elevated PowerShell window so the Altium extension can be registered under ProgramData.'
}
if (Get-Process X2 -ErrorAction SilentlyContinue) { throw 'Close Altium Designer before installing or upgrading EasyEDA Loader.' }
if (Get-Process AltiumLcscBridge -ErrorAction SilentlyContinue) { throw 'Close the old Altium LCSC Bridge companion before upgrading.' }

$registry = Get-ChildItem -Path (Join-Path $env:ProgramData 'Altium') -Filter ExtensionsRegistry.xml -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $registry) { throw 'No installed Altium ExtensionsRegistry.xml was found.' }
$extensionsRoot = Split-Path -Parent $registry.FullName
$adapterRoot = Join-Path $extensionsRoot 'EasyEDA-Loader'
$backupRoot = Join-Path $env:ProgramData 'AltiumLcscBridge\Backups'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$registryBackup = Join-Path $backupRoot "ExtensionsRegistry-$stamp.xml"
Copy-Item -LiteralPath $registry.FullName -Destination $registryBackup -Force

$scriptsRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'AltiumScripts\LcscBridge'
if (Test-Path -LiteralPath $scriptsRoot) {
    Copy-Item -LiteralPath $scriptsRoot -Destination (Join-Path $backupRoot "LcscBridge-Scripts-$stamp") -Recurse -Force
}
if (Test-Path -LiteralPath $adapterRoot) {
    Copy-Item -LiteralPath $adapterRoot -Destination (Join-Path $backupRoot "EasyEDA-Loader-0.2.4-$stamp") -Recurse -Force
}

$stagingRoot = Join-Path $extensionsRoot "EasyEDA-Loader.installing-$stamp"
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
Copy-Item -Path (Join-Path $extensionSource '*') -Destination $stagingRoot -Recurse -Force
foreach ($required in @('EasyEDA-Loader.dll','EasyEDA.Loader.UI.dll','LcscBridge.Core.dll','Microsoft.Web.WebView2.Wpf.dll','Microsoft.Data.Sqlite.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagingRoot $required))) { throw "Staged extension dependency is missing: $required" }
}

[xml]$xml = Get-Content -LiteralPath $registry.FullName -Raw
foreach ($oldName in @('EasyEDA-Loader', 'Altium LCSC Bridge')) {
    $old = $xml.Extensions.SelectSingleNode("Item[@HRID='$oldName']")
    if ($null -ne $old) { [void]$xml.Extensions.RemoveChild($old) }
}
$item = $xml.CreateElement('Item')
$item.SetAttribute('HRID', 'EasyEDA-Loader')
$item.SetAttribute('Guid', '8035C261-E5FE-403B-A9B5-9ABFFB6E0EF5')
function Add-RegistryElement([string]$name, [string]$value) {
    $element = $xml.CreateElement($name); $element.InnerText = $value; [void]$item.AppendChild($element)
}
Add-RegistryElement 'Path' $adapterRoot
Add-RegistryElement 'Status' '0'
Add-RegistryElement 'VaultGuid' ''
Add-RegistryElement 'CreatedBy' 'EasyEDA Loader contributors'
Add-RegistryElement 'CategoryGuid' '793A1F67-0B22-4E01-A5DE-3176A1E8C60D'
Add-RegistryElement 'CategoryName' ''
Add-RegistryElement 'ReadMe' ''
Add-RegistryElement 'Help' ''
Add-RegistryElement 'Requirements' ''
Add-RegistryElement 'Title' 'EasyEDA Loader'
Add-RegistryElement 'ShortDescription' 'Searches EasyEDA/LCSC and creates reusable native Altium libraries.'
Add-RegistryElement 'LongDescription' 'Unified in-process browser, converter, library store, and interactive placement extension.'
Add-RegistryElement 'SmallImage' ''
Add-RegistryElement 'LargeImage' ''
Add-RegistryElement 'Version' '0.3.0.0'
Add-RegistryElement 'VersionGuid' '2219BD53-7254-4B34-93A3-D73D0B06C139'
Add-RegistryElement 'ReleasedDate' ([DateTime]::Today.ToOADate().ToString('F7', [Globalization.CultureInfo]::InvariantCulture))
Add-RegistryElement 'ReleaseNotes' 'Unified non-modal EasyEDA Loader UI, online imports, local cache, WebView2, and native interactive placement.'
Add-RegistryElement 'DateInstalled' ([DateTime]::Now.ToOADate().ToString([Globalization.CultureInfo]::InvariantCulture))
$platformTemplate = $xml.Extensions.Item | Where-Object { $null -ne $_.PlatformVersions } | Select-Object -First 1
if ($null -eq $platformTemplate) { throw 'The Altium registry has no platform-version template.' }
[void]$item.AppendChild($platformTemplate.PlatformVersions.CloneNode($true))
[void]$xml.Extensions.AppendChild($item)

$registryTemp = $registry.FullName + '.easyeda-loader.tmp'
$writerSettings = [Xml.XmlWriterSettings]::new()
$writerSettings.Indent = $true
$writerSettings.IndentChars = '  '
$writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
$writer = [Xml.XmlWriter]::Create($registryTemp, $writerSettings)
try { $xml.Save($writer) } finally { $writer.Dispose() }
[xml]$verification = Get-Content -LiteralPath $registryTemp -Raw -Encoding UTF8
$registered = $verification.Extensions.SelectSingleNode("Item[@HRID='EasyEDA-Loader']")
if ($null -eq $registered -or $registered.Path -ne $adapterRoot -or $registered.Version -ne '0.3.0.0') {
    throw 'Extension registry verification failed; the original registry was preserved.'
}

try {
    if (Test-Path -LiteralPath $adapterRoot) { Remove-Item -LiteralPath $adapterRoot -Recurse -Force }
    Move-Item -LiteralPath $stagingRoot -Destination $adapterRoot
    Move-Item -LiteralPath $registryTemp -Destination $registry.FullName -Force
}
catch {
    Copy-Item -LiteralPath $registryBackup -Destination $registry.FullName -Force
    throw
}

$legacyAdapter = Join-Path $extensionsRoot 'Altium LCSC Bridge'
if (Test-Path -LiteralPath $legacyAdapter) { Remove-Item -LiteralPath $legacyAdapter -Recurse -Force }
$shortcut = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Altium LCSC Bridge.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
$oldCompanion = Join-Path $env:LOCALAPPDATA 'Programs\AltiumLcscBridge'
if (Test-Path -LiteralPath $oldCompanion) { Remove-Item -LiteralPath $oldCompanion -Recurse -Force }
if (Test-Path -LiteralPath $scriptsRoot) { Remove-Item -LiteralPath $scriptsRoot -Recurse -Force }

Write-Host "Installed EasyEDA Loader 0.3.0: $adapterRoot"
Write-Host "Extension registry backup: $registryBackup"
Write-Host 'Restart Altium Designer, then run the existing EasyEDA Loader toolbar command.'
Write-Host 'Documents\LCSC, settings, logs, operation journals, and WebView2 profiles were preserved.'
Write-Host 'If a manually added LCSC Browser button remains, remove it once through Altium Customize; it is no longer used.'
