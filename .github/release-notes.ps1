<#
.SYNOPSIS
  Writes the start of a release's notes: What's new, then the Installers section.

.DESCRIPTION
  What's new is the version's section of CHANGELOG.md: the lines under its "## <version>"
  heading, up to the next "## ". A version without a section gets no What's new, and a warning.
  The Installers section comes from release-notes.md; its SmartScreen sentence is left out once
  every installer given has a valid signature.

  The release workflow puts GitHub's generated changelog after this. Run it locally to preview
  a release's notes before pushing the tag.

.EXAMPLE
  .\.github\release-notes.ps1 -Version 1.1.0
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    # The built installers; with none, the notes describe unsigned ones
    [string[]] $Installers = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$whatsNew = @()
$changelog = Join-Path $root 'CHANGELOG.md'
if (Test-Path $changelog) {
    $inSection = $false
    foreach ($line in Get-Content $changelog -Encoding utf8) {
        if ($line -match '^##\s') {
            if ($inSection) { break }
            # "## 1.1.0", and also "## [1.1.0]" or "## v1.1.0 - 2026-10-07"
            $inSection = $line -match "^##\s+\[?v?$([regex]::Escape($Version))\]?(\s|$)"
        }
        elseif ($inSection) {
            $whatsNew += $line
        }
    }
}
$whatsNewText = ($whatsNew -join "`n").Trim()
if (-not $whatsNewText) {
    $message = "CHANGELOG.md has no section for $Version, so the release notes have no What's new."
    if ($env:GITHUB_ACTIONS) { Write-Host "::warning::$message" } else { Write-Warning $message }
}

$signed = $Installers.Count -gt 0 -and @($Installers | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' }).Count -eq 0
$template = Get-Content (Join-Path $PSScriptRoot 'release-notes.md') -Encoding utf8 |
    Where-Object { -not ($signed -and $_ -match '<!-- unsigned only -->') }
$installersText = (($template -join "`n") -replace ' *<!-- unsigned only -->', '').Replace('{version}', $Version).Trim()

$sections = @()
if ($whatsNewText) { $sections += "## What's new`n`n$whatsNewText" }
$sections += $installersText
($sections -join "`n`n") + "`n"
