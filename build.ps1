<#
.SYNOPSIS
    Build SftpNetDrive and package installers for Windows x64 and ARM64.

.DESCRIPTION
    Produces self-contained single-file executables and Inno Setup installers:
      dist\win-x64\SftpNetDrive.exe
      dist\win-arm64\SftpNetDrive.exe
      dist\SFTP-Net-Drive-Setup-win-x64.exe
      dist\SFTP-Net-Drive-Setup-win-arm64.exe

    Prerequisites:
      - .NET 8 SDK  (https://dotnet.microsoft.com/download)
      - Dokany 2.x  (https://github.com/dokan-dev/dokany/releases)
        Install the x64 or ARM64 MSI before running SftpNet Drive.exe.
      - Inno Setup 7 (optional, required to build installers)
#>

param(
    [string]$Configuration = "Release",
    [switch]$x64Only,
    [switch]$Arm64Only
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "SftpNetDrive\SftpNetDrive.csproj"
$dist = Join-Path $PSScriptRoot "dist"

# Read version from project file; fall back to 1.0.0
$appVersion = try {
    ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
} catch { $null }
if (-not $appVersion) { $appVersion = "1.0.0" }

function Publish($rid) {
    Write-Host "`n==> Publishing $rid ..." -ForegroundColor Cyan
    dotnet publish $proj `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$appVersion `
        -o "$PSScriptRoot\dist\$rid"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }
    Write-Host "==> $rid done -> dist\$rid\SftpNetDrive.exe" -ForegroundColor Green
}

function Get-InnoCompiler {
    $programFiles64 = $env:ProgramFiles
    $programFiles86 = ${env:ProgramFiles(x86)}

    $knownPaths = @(
        Join-Path $programFiles86 'Inno Setup 7\ISCC.exe',
        Join-Path $programFiles64 'Inno Setup 7\ISCC.exe',
        Join-Path $programFiles86 'Inno Setup 6\ISCC.exe',
        Join-Path $programFiles64 'Inno Setup 6\ISCC.exe',
        Join-Path $programFiles86 'Inno Setup 5\ISCC.exe',
        Join-Path $programFiles64 'Inno Setup 5\ISCC.exe'
    )

    foreach ($path in $knownPaths) {
        if ($path -and (Test-Path $path)) {
            return $path
        }
    }

    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Path }

    $searchRoots = @($programFiles64, $programFiles86) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($root in $searchRoots) {
        $candidate = Get-ChildItem -Path $root -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    return $null
}

function CompileInstaller($arch, $sourceExe) {
    if (-not (Test-Path $sourceExe)) {
        throw "Installer source executable not found: $sourceExe"
    }

    $outputBaseName = "SFTP-Net-Drive-Setup-$arch"
    Write-Host "`n==> Compiling installer for $arch ..." -ForegroundColor Cyan

    # Strip "win-" prefix; map x64 -> x64os (Inno Setup 7 identifier)
    $innoArch = ($arch -replace '^win-', '') -replace '^x64$', 'x64os'

    & "$innoCompiler" `
        /dSourceExe="$sourceExe" `
        /dOutputBaseFilename="$outputBaseName" `
        /dAppVersion="$appVersion" `
        /dArch="$innoArch" `
        /O"$dist" `
        "$PSScriptRoot\installer.iss"

    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed for $arch" }
    Write-Host "==> Installer done -> dist\$outputBaseName.exe" -ForegroundColor Green
}

$innoCompiler = Get-InnoCompiler

if (-not $Arm64Only) { Publish "win-x64" }
if (-not $x64Only)   { Publish "win-arm64" }

if ($null -ne $innoCompiler) {
    if (-not $Arm64Only) {
        CompileInstaller "win-x64" "$PSScriptRoot\dist\win-x64\SftpNetDrive.exe"
    }
    if (-not $x64Only) {
        CompileInstaller "win-arm64" "$PSScriptRoot\dist\win-arm64\SftpNetDrive.exe"
    }
}
else {
    Write-Warning "Inno Setup compiler (ISCC.exe) not found. Install Inno Setup to build installers."
}

Write-Host "`nAll builds complete." -ForegroundColor Green
