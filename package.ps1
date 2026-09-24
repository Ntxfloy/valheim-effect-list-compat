param(
    [string]$OutputDirectory = "$PSScriptRoot\dist"
)

$ErrorActionPreference = "Stop"

$dotnetCmd = "dotnet"
$localSdk = Join-Path $env:TEMP 'valheim-dotnet-sdk\dotnet.exe'
$localDotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (Test-Path -LiteralPath $localSdk) {
    $dotnetCmd = $localSdk
} elseif (Test-Path -LiteralPath $localDotnet) {
    $dotnetCmd = $localDotnet
}

Write-Host "Building Patcher..." -ForegroundColor Cyan
& $dotnetCmd build (Join-Path $PSScriptRoot "src\Patcher\ValheimEffectListCompat.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Patcher build failed" }

Write-Host "Building SceneCompat Plugin..." -ForegroundColor Cyan
& $dotnetCmd build (Join-Path $PSScriptRoot "src\SceneCompatPlugin\ValheimSceneCompat.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed" }

$staging = Join-Path $OutputDirectory "thunderstore_package"
$zipPath = Join-Path $OutputDirectory "ValheimEffectListCompat-0.1.5.zip"

if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $staging "BepInEx\patchers\Ntxfloy-ValheimEffectListCompat") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $staging "BepInEx\plugins\Ntxfloy-ValheimSceneCompat") | Out-Null

Copy-Item "$PSScriptRoot\manifest.json", "$PSScriptRoot\README.md", "$PSScriptRoot\icon.png" -Destination $staging
Copy-Item "$PSScriptRoot\src\Patcher\bin\Release\netstandard2.0\ValheimEffectListCompat.dll" -Destination "$staging\BepInEx\patchers\Ntxfloy-ValheimEffectListCompat"
Copy-Item "$PSScriptRoot\src\SceneCompatPlugin\bin\Release\netstandard2.1\ValheimSceneCompat.dll" -Destination "$staging\BepInEx\plugins\Ntxfloy-ValheimSceneCompat"

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path "$staging\*" -DestinationPath $zipPath
Write-Host "Thunderstore package ready: $zipPath" -ForegroundColor Green

$desktopDir = Join-Path $env:USERPROFILE "Desktop"
if (Test-Path $desktopDir) {
    $desktopZip = Join-Path $desktopDir "ValheimEffectListCompat-0.1.5.zip"
    Copy-Item $zipPath -Destination $desktopZip -Force
    Write-Host "Desktop package ready: $desktopZip" -ForegroundColor Green
}
