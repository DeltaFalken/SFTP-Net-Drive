<#
.SYNOPSIS
    Build SftpNetDrive for Windows x64 and ARM64.

.DESCRIPTION
    Produces two self-contained single-file executables:
      dist\win-x64\SftpNetDrive.exe
      dist\win-arm64\SftpNetDrive.exe

    Prerequisites:
      - .NET 8 SDK  (https://dotnet.microsoft.com/download)
      - Dokany 2.x  (https://github.com/dokan-dev/dokany/releases)
        Install the x64 or ARM64 MSI before running SftpNetDrive.exe.
#>

param(
    [string]$Configuration = "Release",
    [switch]$x64Only,
    [switch]$Arm64Only
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "SftpNetDrive\SftpNetDrive.csproj"

function Publish($rid) {
    Write-Host "`n==> Publishing $rid ..." -ForegroundColor Cyan
    dotnet publish $proj `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o "$PSScriptRoot\dist\$rid"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }
    Write-Host "==> $rid done -> dist\$rid\SftpNetDrive.exe" -ForegroundColor Green
}

if (-not $Arm64Only) { Publish "win-x64" }
if (-not $x64Only)   { Publish "win-arm64" }

Write-Host "`nAll builds complete." -ForegroundColor Green
