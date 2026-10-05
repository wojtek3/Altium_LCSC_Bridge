[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRegistry
)

$ErrorActionPreference = 'Stop'
$testDirectory = Join-Path $PSScriptRoot 'registry-test-output'
$resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
$resolvedTestsRoot = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedTestDirectory.StartsWith($resolvedTestsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe registry test directory.'
}
if (Test-Path -LiteralPath $resolvedTestDirectory) { Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $resolvedTestDirectory | Out-Null
$testRegistry = Join-Path $resolvedTestDirectory 'ExtensionsRegistry.xml'
Copy-Item -LiteralPath $SourceRegistry -Destination $testRegistry

[xml]$before = Get-Content -LiteralPath $testRegistry -Raw -Encoding UTF8
$vaultBefore = $before.Extensions.SelectSingleNode("Item[@HRID='VaultExplorer']").OuterXml
$replacedBefore = @($before.Extensions.Item | Where-Object { $_.HRID -in @('EasyEDA-Loader', 'Altium LCSC Bridge') }).Count
[xml]$xml = Get-Content -LiteralPath $testRegistry -Raw -Encoding UTF8
foreach ($oldName in @('EasyEDA-Loader', 'Altium LCSC Bridge')) {
    $old = $xml.Extensions.SelectSingleNode("Item[@HRID='$oldName']")
    if ($null -ne $old) { [void]$xml.Extensions.RemoveChild($old) }
}
$item = $xml.CreateElement('Item')
$item.SetAttribute('HRID', 'EasyEDA-Loader')
$item.SetAttribute('Guid', '8035C261-E5FE-403B-A9B5-9ABFFB6E0EF5')
function Add-TestElement([string]$Name, [string]$Value) {
    $element = $xml.CreateElement($Name)
    $element.InnerText = $Value
    [void]$item.AppendChild($element)
}
$fields = [ordered]@{
    Path = (Join-Path $resolvedTestDirectory 'EasyEDA-Loader'); Status = '0'; VaultGuid = ''
    CreatedBy = 'Altium LCSC Bridge contributors'; CategoryGuid = '793A1F67-0B22-4E01-A5DE-3176A1E8C60D'
    CategoryName = ''; ReadMe = ''; Help = ''; Requirements = ''; Title = 'Altium LCSC Bridge'
    ShortDescription = 'test'; LongDescription = 'test'; SmallImage = ''; LargeImage = ''
    Version = '0.2.4.0'; VersionGuid = '9C148CC2-6D88-483B-A710-BE679D0E5145'
    ReleasedDate = '1'; ReleaseNotes = ''; DateInstalled = '1'
}
foreach ($key in $fields.Keys) { Add-TestElement $key $fields[$key] }
$template = $xml.Extensions.Item | Where-Object { $null -ne $_.PlatformVersions } | Select-Object -First 1
if ($null -eq $template) { throw 'No platform template.' }
[void]$item.AppendChild($template.PlatformVersions.CloneNode($true))
[void]$xml.Extensions.AppendChild($item)
$settings = [Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$writer = [Xml.XmlWriter]::Create($testRegistry, $settings)
try { $xml.Save($writer) } finally { $writer.Dispose() }

[xml]$after = Get-Content -LiteralPath $testRegistry -Raw -Encoding UTF8
$adapter = @($after.Extensions.Item | Where-Object HRID -eq 'EasyEDA-Loader')
if ($adapter.Count -ne 1) { throw "Expected one adapter entry; found $($adapter.Count)." }
if ($before.Extensions.Item.Count - $replacedBefore + 1 -ne $after.Extensions.Item.Count) { throw 'An unrelated extension entry was lost.' }
if ($vaultBefore -ne $after.Extensions.SelectSingleNode("Item[@HRID='VaultExplorer']").OuterXml) { throw 'VaultExplorer entry changed.' }
if ($adapter[0].PlatformVersions.DXP.BuildNumber -ne $template.PlatformVersions.DXP.BuildNumber) { throw 'Platform version was not preserved.' }
Write-Host 'PASS extension registry update preserves VaultExplorer and installed platform versions.'
