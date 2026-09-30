param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "2.4.0",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "v2rayN\v2rayN\v2rayN.csproj"
$issPath = Join-Path $repoRoot "installer\ColituVPN.iss"
$publishDir = Join-Path $repoRoot "artifacts\publish\ColituVPN\$Runtime"
$installerDir = Join-Path $repoRoot "artifacts\installer"
$xraySourceDir = Join-Path $repoRoot "xray-dosyalari"
$singboxSourceDir = Join-Path $repoRoot "singbox-dosyalari"

function Find-InnoCompiler {
    $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    throw "ISCC.exe bulunamadı. Inno Setup 6 kur veya PATH'e ekle."
}

function Assert-FileExists([string]$path, [string]$name) {
    if (-not (Test-Path $path)) {
        throw "$name bulunamadı: $path"
    }
}

Write-Host "Colitu VPN installer build" -ForegroundColor Cyan
Write-Host "Repo: $repoRoot"
Write-Host "Version: $Version"
Write-Host "Runtime: $Runtime"

Assert-FileExists $projectPath "WPF project"
Assert-FileExists $issPath "Inno Setup script"
Assert-FileExists (Join-Path $xraySourceDir "xray.exe") "xray.exe"
Assert-FileExists (Join-Path $xraySourceDir "wintun.dll") "wintun.dll"
Assert-FileExists (Join-Path $xraySourceDir "geoip.dat") "geoip.dat"
Assert-FileExists (Join-Path $xraySourceDir "geosite.dat") "geosite.dat"
Assert-FileExists (Join-Path $singboxSourceDir "sing-box.exe") "sing-box.exe"

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

if (-not $SkipPublish) {
    Write-Host "Publishing app..." -ForegroundColor Cyan
    dotnet publish $projectPath `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        -p:Version=$Version `
        -p:AssemblyVersion=$Version `
        -p:FileVersion=$Version `
        -p:InformationalVersion=$Version `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $publishDir
}

Assert-FileExists (Join-Path $publishDir "ColituVPN.exe") "Published ColituVPN.exe"

$binXrayDir = Join-Path $publishDir "bin\xray"
$setupFallbackDir = Join-Path $publishDir "xray-dosyalari"
New-Item -ItemType Directory -Force -Path $binXrayDir | Out-Null
New-Item -ItemType Directory -Force -Path $setupFallbackDir | Out-Null

Write-Host "Ensuring Xray data files..." -ForegroundColor Cyan
Copy-Item -Path (Join-Path $xraySourceDir "*") -Destination $binXrayDir -Recurse -Force
Copy-Item -Path (Join-Path $xraySourceDir "*") -Destination $setupFallbackDir -Recurse -Force

Assert-FileExists (Join-Path $binXrayDir "xray.exe") "Published bin\xray\xray.exe"
Assert-FileExists (Join-Path $binXrayDir "geoip.dat") "Published bin\xray\geoip.dat"
Assert-FileExists (Join-Path $binXrayDir "geosite.dat") "Published bin\xray\geosite.dat"
Assert-FileExists (Join-Path $binXrayDir "wintun.dll") "Published bin\xray\wintun.dll"

$binSingboxDir = Join-Path $publishDir "bin\sing_box"
New-Item -ItemType Directory -Force -Path $binSingboxDir | Out-Null
Copy-Item -Path (Join-Path $singboxSourceDir "*") -Destination $binSingboxDir -Recurse -Force -Exclude "README.md"
Copy-Item -Path (Join-Path $xraySourceDir "wintun.dll") -Destination $binSingboxDir -Force
Assert-FileExists (Join-Path $binSingboxDir "sing-box.exe") "Published bin\sing_box\sing-box.exe"

$iscc = Find-InnoCompiler
Write-Host "Using Inno compiler: $iscc" -ForegroundColor Cyan

$env:COLITU_APP_VERSION = $Version
$env:COLITU_PUBLISH_DIR = $publishDir
$env:COLITU_OUTPUT_DIR = $installerDir

Write-Host "Compiling installer..." -ForegroundColor Cyan
& $iscc $issPath

$installerPath = Join-Path $installerDir "ColituVPN-Setup-$Version-x64.exe"
Assert-FileExists $installerPath "Installer"

$hash = Get-FileHash $installerPath -Algorithm SHA256

# Release manifest read by the in-app updater from https://colitu.com/downloads/windows/latest.json.
# Upload the installer and this file together to the website's downloads/windows/ folder.
$versionParts = $Version.Split('.') | ForEach-Object { [int]$_ }
$versionCode = ($versionParts[0] * 100) + ($versionParts[1] * 10) + $versionParts[2]
$manifest = [ordered]@{
    latestVersionCode = $versionCode
    versionName = $Version
    downloadUrl = "https://colitu.com/downloads/windows/ColituVPN-Setup-$Version-x64.exe"
    sha256 = $hash.Hash.ToLowerInvariant()
    forceUpdate = $false
    releaseNotes = ""
}
$stableInstallerPath = Join-Path $installerDir "ColituVPN-Setup-x64.exe"
Copy-Item -Path $installerPath -Destination $stableInstallerPath -Force
$manifestPath = Join-Path $installerDir "latest.json"
$manifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host ""
Write-Host "DONE" -ForegroundColor Green
Write-Host "Installer: $installerPath"
Write-Host "SHA256: $($hash.Hash)"
Write-Host "Stable download: $stableInstallerPath"
Write-Host "Update manifest: $manifestPath"
Write-Host "Upload all three files to <install-dir>/public-downloads/windows/ on the Colitu server."
