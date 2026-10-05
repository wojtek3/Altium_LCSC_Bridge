[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'AltiumLcscBridge.exe')) { $source = $PSScriptRoot }
$executable = Join-Path $source 'AltiumLcscBridge.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw 'Published application not found. Run build\Build.ps1 first, then run artifacts\publish\Install.ps1.'
}
$adapterSource = Join-Path $source 'altium-extension'
if (-not (Test-Path -LiteralPath (Join-Path $adapterSource 'EasyEDA-Loader.dll'))) {
    throw 'Compiled Altium adapter not found in the release.'
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installation requires an elevated PowerShell window so the Altium extension can be registered under ProgramData.'
}
if (Get-Process X2 -ErrorAction SilentlyContinue) { throw 'Close Altium Designer before installing or upgrading the bridge.' }

$registry = Get-ChildItem -Path (Join-Path $env:ProgramData 'Altium') -Filter ExtensionsRegistry.xml -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $registry) { throw 'No installed Altium ExtensionsRegistry.xml was found.' }
$extensionsRoot = Split-Path -Parent $registry.FullName
$adapterRoot = Join-Path $extensionsRoot 'EasyEDA-Loader'
$backupRoot = Join-Path $env:ProgramData 'AltiumLcscBridge\Backups'
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$backup = Join-Path $backupRoot ("ExtensionsRegistry-{0}.xml" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
Copy-Item -LiteralPath $registry.FullName -Destination $backup -Force

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\AltiumLcscBridge'
$scriptsRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'AltiumScripts\LcscBridge'
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
Get-Process AltiumLcscBridge -ErrorAction SilentlyContinue | Stop-Process -Force

Get-ChildItem -LiteralPath $source | Where-Object Name -NotIn @('Install.ps1','Uninstall.ps1','altium','altium-extension','source') |
    Copy-Item -Destination $installRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $source 'Uninstall.ps1') -Destination $installRoot -Force
Copy-Item -Path (Join-Path $source 'altium\*') -Destination $scriptsRoot -Recurse -Force
New-Item -ItemType Directory -Path $adapterRoot -Force | Out-Null
Copy-Item -Path (Join-Path $adapterSource '*') -Destination $adapterRoot -Recurse -Force

[xml]$xml = Get-Content -LiteralPath $registry.FullName -Raw
$oldNames = @('EasyEDA-Loader', 'Altium LCSC Bridge')
foreach ($oldName in $oldNames) {
    $old = $xml.Extensions.SelectSingleNode("Item[@HRID='$oldName']")
    if ($null -ne $old) { [void]$xml.Extensions.RemoveChild($old) }
}
$item = $xml.CreateElement('Item')
$item.SetAttribute('HRID', 'EasyEDA-Loader')
$item.SetAttribute('Guid', '8035C261-E5FE-403B-A9B5-9ABFFB6E0EF5')
function Add-RegistryElement([string]$name, [string]$value) {
    $element = $xml.CreateElement($name)
    $element.InnerText = $value
    [void]$item.AppendChild($element)
}
Add-RegistryElement 'Path' $adapterRoot
Add-RegistryElement 'Status' '0'
Add-RegistryElement 'VaultGuid' ''
Add-RegistryElement 'CreatedBy' 'Altium LCSC Bridge contributors'
Add-RegistryElement 'CategoryGuid' '793A1F67-0B22-4E01-A5DE-3176A1E8C60D'
Add-RegistryElement 'CategoryName' ''
Add-RegistryElement 'ReadMe' ''
Add-RegistryElement 'Help' ''
Add-RegistryElement 'Requirements' ''
Add-RegistryElement 'Title' 'Altium LCSC Bridge'
Add-RegistryElement 'ShortDescription' 'Creates native Altium libraries from validated EasyEDA/LCSC component models.'
Add-RegistryElement 'LongDescription' 'Native library creation adapter for the Altium LCSC Bridge companion.'
Add-RegistryElement 'SmallImage' ''
Add-RegistryElement 'LargeImage' ''
Add-RegistryElement 'Version' '0.2.4.0'
Add-RegistryElement 'VersionGuid' '9C148CC2-6D88-483B-A710-BE679D0E5145'
Add-RegistryElement 'ReleasedDate' ([DateTime]::Today.ToOADate().ToString('F7', [Globalization.CultureInfo]::InvariantCulture))
Add-RegistryElement 'ReleaseNotes' 'Protocol-3 operation status, transactional Altium cleanup, visible failures, and ComponentPolarityLayer support.'
Add-RegistryElement 'DateInstalled' ([DateTime]::Now.ToOADate().ToString([Globalization.CultureInfo]::InvariantCulture))
$platformTemplate = $xml.Extensions.Item | Where-Object { $null -ne $_.PlatformVersions } | Select-Object -First 1
if ($null -eq $platformTemplate) { throw 'The Altium registry has no platform-version template.' }
$platforms = $platformTemplate.PlatformVersions.CloneNode($true)
[void]$item.AppendChild($platforms)
[void]$xml.Extensions.AppendChild($item)
$registryTemp = $registry.FullName + '.lcsc-bridge.tmp'
$writerSettings = [Xml.XmlWriterSettings]::new()
$writerSettings.Indent = $true
$writerSettings.IndentChars = '  '
$writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
$writer = [Xml.XmlWriter]::Create($registryTemp, $writerSettings)
try { $xml.Save($writer) } finally { $writer.Dispose() }
[xml]$verification = Get-Content -LiteralPath $registryTemp -Raw -Encoding UTF8
$registered = $verification.Extensions.SelectSingleNode("Item[@HRID='EasyEDA-Loader']")
if ($null -eq $registered -or $registered.Path -ne $adapterRoot) { throw 'Extension registry verification failed; the original registry was preserved.' }
Move-Item -LiteralPath $registryTemp -Destination $registry.FullName -Force

$staleAdapterRoot = Join-Path $extensionsRoot 'Altium LCSC Bridge'
if ((Test-Path -LiteralPath $staleAdapterRoot) -and $staleAdapterRoot -ne $adapterRoot) {
    Remove-Item -LiteralPath $staleAdapterRoot -Recurse -Force
}

$shortcutPath = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Altium LCSC Bridge.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installRoot 'AltiumLcscBridge.exe'
$shortcut.WorkingDirectory = $installRoot
$shortcut.Description = 'Browse and place cached LCSC components in Altium Designer'
$shortcut.Save()

Start-Process -FilePath (Join-Path $installRoot 'AltiumLcscBridge.exe')
Write-Host "Installed application: $installRoot"
Write-Host "Installed Altium script: $scriptsRoot\LcscBridge.PrjScr"
Write-Host "Toolbar icon: $scriptsRoot\lcsc-bridge.bmp"
Write-Host "Installed Altium adapter: $adapterRoot"
Write-Host "Extension registry backup: $backup"
Write-Host 'Restart Altium Designer to load the adapter.'
Write-Host 'The component library under Documents\LCSC is never removed by this installer.'
