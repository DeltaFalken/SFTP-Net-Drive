param(
    [string]$Configuration = "Release",
    [switch]$x64Only,
    [switch]$Arm64Only
)

$ErrorActionPreference = "Stop"

$projEXE = Join-Path $PSScriptRoot "SftpNetDrive\SftpNetDrive.csproj"
$projNP  = Join-Path $PSScriptRoot "SftpNetDriveNP\SftpNetDriveNP.csproj"
$dist    = Join-Path $PSScriptRoot "dist"

$appVersion = try {
    ([xml](Get-Content $projEXE)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
} catch { $null }
if (-not $appVersion) { $appVersion = "2.0.0" }

# --- MSVC / Windows SDK environment setup ---

function Get-MsvcCfg($targetArch) {
    $msvcRoot = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*" `
        -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $msvcRoot) { return $null }

    $hostSuffix = if ($targetArch -eq "arm64") { "arm64" } else { "x64" }
    $linkDir = "$msvcRoot\bin\Hostx64\$hostSuffix"
    if (-not (Test-Path "$linkDir\link.exe")) { return $null }

    $libArch = if ($targetArch -eq "arm64") { "arm64" } else { "x64" }

    $sdkProp = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots" -ErrorAction SilentlyContinue
    $sdkRoot = if ($sdkProp) { $sdkProp.KitsRoot10.TrimEnd("\") } else { $null }
    $sdkVer  = Get-ChildItem "$sdkRoot\Lib" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name

    if (-not (Test-Path "$msvcRoot\lib\$libArch\libcmt.lib")) {
        throw "MSVC $targetArch CRT libs not found (libcmt.lib missing). Install 'MSVC v14x - VS C++ ARM64/ARM64EC build tools' via VS Installer."
    }
    if (-not $sdkVer -or -not (Test-Path "$sdkRoot\Lib\$sdkVer\um\$libArch\advapi32.lib")) {
        throw "Windows SDK $libArch libs not found. Install 'Windows 11 SDK' via VS Installer."
    }

    return @{
        LinkDir = $linkDir
        MsvcDir = $msvcRoot
        SdkRoot = $sdkRoot
        SdkVer  = $sdkVer
        LibArch = $libArch
    }
}

function Set-MsvcCfg($cfg) {
    $la = $cfg.LibArch
    $env:PATH              = $cfg.LinkDir + ";" + $env:PATH
    $env:LIB               = $cfg.MsvcDir + "\lib\$la;" + $cfg.SdkRoot + "\Lib\" + $cfg.SdkVer + "\um\$la;" + $cfg.SdkRoot + "\Lib\" + $cfg.SdkVer + "\ucrt\$la"
    $env:INCLUDE           = $cfg.MsvcDir + "\include;" + $cfg.SdkRoot + "\Include\" + $cfg.SdkVer + "\um;" + $cfg.SdkRoot + "\Include\" + $cfg.SdkVer + "\ucrt;" + $cfg.SdkRoot + "\Include\" + $cfg.SdkVer + "\shared"
    $env:VCToolsInstallDir = $cfg.MsvcDir + "\"
}

# --- Publish functions ---

function Publish-EXE($rid) {
    Write-Host "`n==> Publishing SftpNetDrive ($rid) ..." -ForegroundColor Cyan
    dotnet publish $projEXE `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$appVersion `
        -o "$PSScriptRoot\dist\$rid"
    if ($LASTEXITCODE -ne 0) { throw "EXE publish failed for $rid" }
    Write-Host "==> $rid EXE done -> dist\$rid\SftpNetDrive.exe" -ForegroundColor Green
}

function Publish-NP($rid) {
    $arch = $rid -replace "^win-", ""
    Write-Host "`n==> Publishing SftpNetDriveNP NativeAOT DLL ($rid) ..." -ForegroundColor Cyan

    $cfg = Get-MsvcCfg $arch
    Set-MsvcCfg $cfg

    dotnet publish $projNP `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:Version=$appVersion `
        -p:IlcUseEnvironmentalTools=true `
        -o "$PSScriptRoot\dist\$rid"
    if ($LASTEXITCODE -ne 0) { throw "NP DLL publish failed for $rid" }
    Write-Host "==> $rid NP DLL done -> dist\$rid\SftpNetDriveNP.dll" -ForegroundColor Green
}

# --- Inno Setup ---

function Get-InnoCompiler {
    $p86 = ${env:ProgramFiles(x86)}
    $p64 = $env:ProgramFiles
    @(
        (Join-Path $p64 "Inno Setup 7\ISCC.exe"),
        (Join-Path $p86 "Inno Setup 7\ISCC.exe"),
        (Join-Path $p64 "Inno Setup 6\ISCC.exe"),
        (Join-Path $p86 "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

function Invoke-InstallerBuild($arch, $sourceExe, $sourceNPDll) {
    if (-not (Test-Path $sourceExe))   { throw "Not found: $sourceExe" }
    if (-not (Test-Path $sourceNPDll)) { throw "Not found: $sourceNPDll" }
    $outputName = "SFTP-Net-Drive-Setup-$arch"
    Write-Host "`n==> Compiling installer for $arch ..." -ForegroundColor Cyan
    $innoArch = ($arch -replace "^win-", "") -replace "^x64$", "x64os"
    & "$innoCompiler" `
        /dSourceExe="$sourceExe" `
        /dSourceNPDll="$sourceNPDll" `
        /dOutputBaseFilename="$outputName" `
        /dAppVersion="$appVersion" `
        /dArch="$innoArch" `
        /O"$dist" `
        "$PSScriptRoot\installer.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $arch" }
    Write-Host "==> Installer done -> dist\$outputName.exe" -ForegroundColor Green
}

# --- Main ---

$innoCompiler = Get-InnoCompiler

if (-not $Arm64Only) { Publish-EXE "win-x64";   Publish-NP "win-x64" }
if (-not $x64Only)   { Publish-EXE "win-arm64";  Publish-NP "win-arm64" }

if ($null -ne $innoCompiler) {
    if (-not $Arm64Only) {
        Invoke-InstallerBuild "win-x64" `
            "$PSScriptRoot\dist\win-x64\SftpNetDrive.exe" `
            "$PSScriptRoot\dist\win-x64\SftpNetDriveNP.dll"
    }
    if (-not $x64Only) {
        Invoke-InstallerBuild "win-arm64" `
            "$PSScriptRoot\dist\win-arm64\SftpNetDrive.exe" `
            "$PSScriptRoot\dist\win-arm64\SftpNetDriveNP.dll"
    }
} else {
    Write-Warning "Inno Setup not found -- installers skipped."
}

Write-Host "`nAll builds complete." -ForegroundColor Green