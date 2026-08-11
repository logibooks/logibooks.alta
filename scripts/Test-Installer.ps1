[CmdletBinding()]
param(
    [string] $MsiPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Logibooks.Alta.Setup\Release\Logibooks.Alta.Adapter.Setup.msi')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedMsi = (Resolve-Path $MsiPath).Path
$props = [xml] [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'Directory.Build.props'))
$expectedVersion = $props.Project.PropertyGroup.Version

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($resolvedMsi, 0)

function Read-Rows {
    param(
        [Parameter(Mandatory)] [string] $Sql,
        [Parameter(Mandatory)] [int] $ColumnCount
    )

    $view = $database.OpenView($Sql)
    [void] $view.Execute()
    $rows = @()
    while ($record = $view.Fetch()) {
        if ($ColumnCount -eq 1) {
            $rows += [string] $record.StringData(1)
        }
        else {
            $rows += [pscustomobject]@{
                First = [string] $record.StringData(1)
                Second = [string] $record.StringData(2)
            }
        }
    }
    [void] $view.Close()
    $rows
}

$properties = @{}
foreach ($row in $(Read-Rows -Sql 'SELECT `Property`,`Value` FROM `Property`' -ColumnCount 2)) {
    $properties[$row.First] = $row.Second
}

$expectedProperties = @{
    ProductName = 'Logibooks Alta Adapter'
    ProductVersion = $expectedVersion
    UpgradeCode = '{36E2F8BB-D73C-47F2-A624-371518531952}'
    ALLUSERS = '2'
}
foreach ($entry in $expectedProperties.GetEnumerator()) {
    if ($properties[$entry.Key] -ne $entry.Value) {
        throw "MSI property '$($entry.Key)' is '$($properties[$entry.Key])'; expected '$($entry.Value)'."
    }
}

$template = $database.SummaryInformation(0).Property(7)
if (-not $template.StartsWith('x64;', [StringComparison]::OrdinalIgnoreCase)) {
    throw "MSI template '$template' is not x64."
}

$customActionTargets = @(
    (Read-Rows -Sql 'SELECT `Target` FROM `CustomAction`' -ColumnCount 1) |
        ForEach-Object { $_ }
)
foreach ($target in '/Install', '/Rollback', '/Uninstall') {
    if ($target -notin $customActionTargets) {
        throw "MSI custom action '$target' is missing."
    }
}

$fileNames = @(
    (Read-Rows -Sql 'SELECT `FileName` FROM `File`' -ColumnCount 1) |
        ForEach-Object { $_.Split('|')[-1] }
)
foreach ($fileName in 'Logibooks.Alta.Adapter.exe', 'Logibooks.Alta.Adapter.ico', 'appsettings.template.json') {
    if ($fileName -notin $fileNames) {
        throw "MSI payload '$fileName' is missing."
    }
}

$iconNames = @(Read-Rows -Sql 'SELECT `Name` FROM `Icon`' -ColumnCount 1)
if ([string]::IsNullOrWhiteSpace($properties.ARPPRODUCTICON)) {
    throw 'MSI ARPPRODUCTICON property is missing.'
}
if ($properties.ARPPRODUCTICON -notin $iconNames) {
    throw "MSI product icon '$($properties.ARPPRODUCTICON)' is missing from the Icon table."
}

Write-Host "MSI validation passed: $resolvedMsi ($expectedVersion, $template)"
