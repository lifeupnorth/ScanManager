<#
.SYNOPSIS
  Signs files with the code-signing method configured in environment variables.

.DESCRIPTION
  Called by Inno Setup (through the SignTool directive) to sign the installer and the
  uninstaller, and usable by hand. See signing.ps1 for the settings.

.EXAMPLE
  .\installer\sign.ps1 artifacts\installers\ScanManager-0.1.0-x64-setup.exe
#>
param(
    [Parameter(Mandatory, ValueFromRemainingArguments)]
    [string[]] $Path
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'signing.ps1')
Invoke-Signing $Path
