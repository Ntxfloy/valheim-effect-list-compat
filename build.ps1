param(
    [string]$Profile = "$env:APPDATA\r2modmanPlus-local\Valheim\profiles\Default",
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$localSdk = Join-Path $env:TEMP 'valheim-dotnet-sdk\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }

Write-Host "Building Patcher..." -ForegroundColor Cyan
& $dotnet build (Join-Path $root 'src\Patcher\ValheimEffectListCompat.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Patcher compilation failed" }

Write-Host "Building SceneCompatPlugin..." -ForegroundColor Cyan
& $dotnet build (Join-Path $root 'src\SceneCompatPlugin\ValheimSceneCompat.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Plugin compilation failed" }

# Package assembly
$patcherDll = Join-Path $root 'src\Patcher\bin\Release\netstandard2.0\ValheimEffectListCompat.dll'
$patcherPdb = Join-Path $root 'src\Patcher\bin\Release\netstandard2.0\ValheimEffectListCompat.pdb'
$pluginDll = Join-Path $root 'src\SceneCompatPlugin\bin\Release\netstandard2.1\ValheimSceneCompat.dll'
$pluginPdb = Join-Path $root 'src\SceneCompatPlugin\bin\Release\netstandard2.1\ValheimSceneCompat.pdb'

$pkgPatcherDir = Join-Path $root 'package\BepInEx\patchers\Ntxfloy-ValheimEffectListCompat'
$pkgPluginDir = Join-Path $root 'package\BepInEx\plugins\Ntxfloy-ValheimSceneCompat'
New-Item -ItemType Directory -Path $pkgPatcherDir -Force | Out-Null
New-Item -ItemType Directory -Path $pkgPluginDir -Force | Out-Null

Copy-Item (Join-Path $root 'README.md') -Destination (Join-Path $root 'package\README.md') -Force
Copy-Item (Join-Path $root 'manifest.json') -Destination (Join-Path $root 'package\manifest.json') -Force

Copy-Item $patcherDll -Destination $pkgPatcherDir -Force
if (Test-Path $patcherPdb) { Copy-Item $patcherPdb -Destination $pkgPatcherDir -Force } else { Get-ChildItem $pkgPatcherDir -Filter *.pdb -Recurse | Remove-Item -Force }
Copy-Item $pluginDll -Destination $pkgPluginDir -Force
if (Test-Path $pluginPdb) { Copy-Item $pluginPdb -Destination $pkgPluginDir -Force } else { Get-ChildItem $pkgPluginDir -Filter *.pdb -Recurse | Remove-Item -Force }

# Update zip archive
$zipPath = Join-Path $root 'Ntxfloy-ValheimEffectListCompat-0.1.5-test.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $root 'package\*') -DestinationPath $zipPath
Write-Host "Successfully packaged $zipPath" -ForegroundColor Green

if ($Install) {
    Write-Host "Installing to r2modman profile: $Profile" -ForegroundColor Cyan
    $dstPatcher = Join-Path $Profile 'BepInEx\patchers\Ntxfloy-Ntxfloy-ValheimEffectListCompat\Ntxfloy-ValheimEffectListCompat'
    $dstPlugin = Join-Path $Profile 'BepInEx\plugins\Ntxfloy-ValheimSceneCompat'
    New-Item -ItemType Directory -Path $dstPatcher -Force | Out-Null
    New-Item -ItemType Directory -Path $dstPlugin -Force | Out-Null

    Copy-Item $patcherDll -Destination $dstPatcher -Force
    if (Test-Path $patcherPdb) { Copy-Item $patcherPdb -Destination $dstPatcher -Force }
    Copy-Item $pluginDll -Destination $dstPlugin -Force
    if (Test-Path $pluginPdb) { Copy-Item $pluginPdb -Destination $dstPlugin -Force }
    Write-Host "Deployment completed!" -ForegroundColor Green
}