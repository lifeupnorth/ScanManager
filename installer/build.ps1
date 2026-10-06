<#
.SYNOPSIS
  Builds the ScanManager installers.

.DESCRIPTION
  Publishes the app self-contained for each runtime (so users don't need .NET installed)
  and compiles one Inno Setup installer per architecture into artifacts\installers.

.EXAMPLE
  .\installer\build.ps1
  .\installer\build.ps1 -Runtime win-arm64
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64', 'win-arm64'),
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$output = Join-Path $artifacts 'installers'

function Invoke-Checked([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

# The version comes from Directory.Build.props so the app and the installer always agree.
[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = @($props.Project.PropertyGroup.Version)[0]
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

# Fetch the Inno Setup compiler from NuGet.
$innoProject = Join-Path $PSScriptRoot 'InnoSetup.csproj'
[xml] $innoXml = Get-Content $innoProject
$innoVersion = @($innoXml.Project.PropertyGroup.InnoSetupVersion)[0]
Invoke-Checked 'Downloading Inno Setup' { dotnet restore $innoProject --verbosity quiet }
$packages = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
$iscc = Join-Path $packages.Trim() "tools.innosetup\$innoVersion\tools\ISCC.exe"
if (-not (Test-Path $iscc)) { throw "ISCC.exe not found at $iscc." }

New-Item -ItemType Directory -Force $output | Out-Null

foreach ($rid in $Runtime) {
    $arch = $rid -replace '^win-', ''
    $publishDir = Join-Path $artifacts "publish\$rid"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    Write-Host "Publishing ScanManager $version for $rid..."
    Invoke-Checked "Publishing for $rid" {
        dotnet publish (Join-Path $root 'src\ScanManager.App\ScanManager.App.csproj') `
            --configuration $Configuration --runtime $rid --self-contained `
            -p:PublishReadyToRun=true -p:DebugType=none `
            --output $publishDir --verbosity quiet
    }

    Write-Host "Compiling the $arch installer..."
    Invoke-Checked "Compiling the $arch installer" {
        & $iscc /Qp "/DAppVersion=$version" "/DArch=$arch" "/DSourceDir=$publishDir" "/O$output" `
            (Join-Path $PSScriptRoot 'ScanManager.iss')
    }
}

Get-ChildItem $output -Filter "ScanManager-$version-*-setup.exe" |
    Select-Object Name, @{ Name = 'Size (MB)'; Expression = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
