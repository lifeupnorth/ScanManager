<#
  Authenticode signing for ScanManager builds, dot-sourced by build.ps1 and sign.ps1.

  Signing is off unless one of these is configured through environment variables
  (empty values count as unset, so CI can always pass them through):

  Azure Artifact Signing (formerly Trusted Signing)
    SIGN_ARTIFACT_ENDPOINT   account endpoint, e.g. https://eus.codesigning.azure.net
    SIGN_ARTIFACT_ACCOUNT    code signing account name
    SIGN_ARTIFACT_PROFILE    certificate profile name
    Azure credentials are found the usual way (DefaultAzureCredential): AZURE_TENANT_ID,
    AZURE_CLIENT_ID and AZURE_CLIENT_SECRET for a service principal, or `az login` locally.

  Certificate file
    SIGN_CERT_FILE           path to a .pfx file   (or SIGN_CERT_PFX_BASE64: the file, base64-encoded)
    SIGN_CERT_PASSWORD       its password

  Certificate in the Windows certificate store (current user)
    SIGN_CERT_THUMBPRINT     SHA-1 thumbprint. Also covers USB tokens and cloud-HSM tools that
                             register their certificate in the store (e.g. via a KSP).

  Optional
    SIGN_TIMESTAMP_URL       RFC 3161 timestamp server. Defaults to Microsoft's for Artifact
                             Signing and DigiCert's otherwise.
#>

function Get-SigningSetting([string] $name) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value.Trim()
}

<# Returns 'ArtifactSigning', 'CertificateFile', 'CertificateStore', or $null when signing is off. #>
function Get-SigningMethod {
    if ((Get-SigningSetting 'SIGN_ARTIFACT_ENDPOINT') -and (Get-SigningSetting 'SIGN_ARTIFACT_ACCOUNT') -and (Get-SigningSetting 'SIGN_ARTIFACT_PROFILE')) {
        return 'ArtifactSigning'
    }
    if ((Get-SigningSetting 'SIGN_CERT_FILE') -or (Get-SigningSetting 'SIGN_CERT_PFX_BASE64')) { return 'CertificateFile' }
    if (Get-SigningSetting 'SIGN_CERT_THUMBPRINT') { return 'CertificateStore' }
    return $null
}

<# Downloads signtool (and the Artifact Signing client) from NuGet and returns their paths. #>
function Get-SigningTools {
    $project = Join-Path $PSScriptRoot 'SigningTools.csproj'
    dotnet restore $project --verbosity quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Downloading the signing tools failed.' }

    [xml] $xml = Get-Content $project
    $group = $xml.Project.PropertyGroup
    $packages = ((dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
    $sdk = Join-Path $packages "microsoft.windows.sdk.buildtools\$(@($group.WindowsSdkBuildToolsVersion)[0])\bin"
    $client = Join-Path $packages "microsoft.artifactsigning.client\$(@($group.ArtifactSigningClientVersion)[0])\bin\x64"

    # The Artifact Signing plug-in is x64-only, so that method always uses the x64 signtool
    # (Windows on ARM runs it under emulation). Otherwise use the machine's own architecture.
    $arch = if ((Get-SigningMethod) -ne 'ArtifactSigning' -and $env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $signtool = Get-ChildItem $sdk -Recurse -Filter signtool.exe | Where-Object { $_.Directory.Name -eq $arch } |
        Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw "signtool.exe ($arch) not found under $sdk." }

    [pscustomobject] @{
        SignTool = $signtool.FullName
        ArtifactSigningDlib = Join-Path $client 'Azure.CodeSigning.Dlib.dll'
    }
}

<# Signs the given files in place with the configured method. #>
function Invoke-Signing([string[]] $Files) {
    $method = Get-SigningMethod
    if (-not $method) { throw 'Code signing is not configured (see installer\signing.ps1).' }
    if (-not $Files) { return }

    $tools = Get-SigningTools
    $temp = @()
    try {
        $arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256')
        switch ($method) {
            'ArtifactSigning' {
                $metadata = [IO.Path]::GetTempFileName()
                $temp += $metadata
                @{
                    Endpoint               = Get-SigningSetting 'SIGN_ARTIFACT_ENDPOINT'
                    CodeSigningAccountName = Get-SigningSetting 'SIGN_ARTIFACT_ACCOUNT'
                    CertificateProfileName = Get-SigningSetting 'SIGN_ARTIFACT_PROFILE'
                } | ConvertTo-Json | Set-Content -Path $metadata -Encoding ASCII
                $timestamp = Get-SigningSetting 'SIGN_TIMESTAMP_URL'
                if (-not $timestamp) { $timestamp = 'http://timestamp.acs.microsoft.com' }
                $arguments += @('/tr', $timestamp, '/dlib', $tools.ArtifactSigningDlib, '/dmdf', $metadata)
            }
            'CertificateFile' {
                $file = Get-SigningSetting 'SIGN_CERT_FILE'
                if (-not $file) {
                    $file = Join-Path ([IO.Path]::GetTempPath()) "$([guid]::NewGuid().ToString('N')).pfx"
                    $temp += $file
                    [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String((Get-SigningSetting 'SIGN_CERT_PFX_BASE64')))
                }
                $arguments += @('/tr', (Get-TimestampUrl), '/f', $file)
                $password = Get-SigningSetting 'SIGN_CERT_PASSWORD'
                if ($password) { $arguments += @('/p', $password) }
            }
            'CertificateStore' {
                $arguments += @('/tr', (Get-TimestampUrl), '/sha1', (Get-SigningSetting 'SIGN_CERT_THUMBPRINT'))
            }
        }

        # Timestamp servers occasionally fail; retry those a couple of times. Other errors
        # (wrong password, missing certificate) won't fix themselves, so fail straight away.
        for ($attempt = 1; ; $attempt++) {
            $output = & $tools.SignTool @arguments @Files 2>&1
            if ($LASTEXITCODE -eq 0) { break }
            if ($attempt -ge 3 -or ($output -join "`n") -notmatch 'timestamp') {
                # Never echo the arguments: they can contain the certificate password.
                throw "signtool failed (exit code $LASTEXITCODE):`n$($output -join "`n")"
            }
            Write-Warning "Signing attempt $attempt failed; retrying..."
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
    finally {
        $temp | ForEach-Object { Remove-Item $_ -Force -ErrorAction SilentlyContinue }
    }
}

function Get-TimestampUrl {
    $url = Get-SigningSetting 'SIGN_TIMESTAMP_URL'
    if ($url) { return $url }
    return 'http://timestamp.digicert.com'
}
