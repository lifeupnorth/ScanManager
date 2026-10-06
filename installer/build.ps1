<#
.SYNOPSIS
  Builds the ScanManager installers.

.DESCRIPTION
  Publishes the app self-contained for each runtime (so users don't need .NET installed)
  and compiles one Inno Setup installer per architecture into artifacts\installers.

  When code signing is configured (see signing.ps1), every executable and DLL the app ships
  without a valid signature, the installer and the uninstaller are all signed. Otherwise the
  build is unsigned.

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
. (Join-Path $PSScriptRoot 'signing.ps1')
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

$signing = Get-SigningMethod
if ($signing) {
    Write-Host "Code signing: $signing"
    $null = Get-SigningTools # download once up front
    # Inno Setup runs this for the installer and the embedded uninstaller ($q = quote, $f = file).
    $signCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `$q$(Join-Path $PSScriptRoot 'sign.ps1')`$q `$f"
}
else {
    Write-Host 'Code signing: off (configure it as described in installer\signing.ps1)'
}

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

    if ($signing) {
        # Sign whatever has no valid signature, not just our own files: ReadyToRun rewrites the
        # NuGet DLLs it compiles (CommunityToolkit.Mvvm and others), which drops their signatures.
        # Smart App Control can block an app that loads unsigned DLLs.
        # The .NET runtime files come precompiled and keep Microsoft's signatures.
        $unsigned = Get-ChildItem $publishDir -File -Recurse |
            Where-Object { $_.Extension -in '.exe', '.dll' -and (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' }
        Write-Host "Signing $(@($unsigned).Count) unsigned app files..."
        Invoke-Signing $unsigned.FullName
    }

    Write-Host "Compiling the $arch installer..."
    $isccArgs = @('/Qp', "/DAppVersion=$version", "/DArch=$arch", "/DSourceDir=$publishDir", "/O$output")
    if ($signing) { $isccArgs += @('/DSign', "/Ssigntool=$signCommand") }
    Invoke-Checked "Compiling the $arch installer" {
        & $iscc @isccArgs (Join-Path $PSScriptRoot 'ScanManager.iss')
    }
}

# Report what was built and whether it is signed, so an unsigned release can't slip out unnoticed.
$Runtime | ForEach-Object { Get-Item (Join-Path $output "ScanManager-$version-$($_ -replace '^win-', '')-setup.exe") } |
    Select-Object Name,
        @{ Name = 'Size (MB)'; Expression = { [math]::Round($_.Length / 1MB, 1) } },
        @{ Name = 'Signed by'; Expression = { $cert = (Get-AuthenticodeSignature $_.FullName).SignerCertificate; if ($cert) { $cert.Subject } else { '(not signed)' } } } |
    Format-Table -AutoSize
