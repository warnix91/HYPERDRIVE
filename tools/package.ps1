# Builds Hyperdrive (Release) and makes the player archive dist\Hyperdrive-<version>.zip (Windows twin of tools/package.sh):
#   GameData/Hyperdrive (DLLs, config, localization, shaders, version file) + README.md + CHANGELOG.md (+ LICENSE if present).
# Never includes PluginData (player settings, caches, learned data) nor anything from _reference/.
# Zip entries use forward slashes (written one by one below), so the archive opens on Mac and Linux.
# usage: powershell -ExecutionPolicy Bypass -File tools\package.ps1
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

dotnet build Hyperdrive.sln -c Release -p:DeployToKSP=false | Select-String -Pattern " error |Warn|->"
if ($LASTEXITCODE -ne 0) { Write-Host "build failed"; exit 1 }

$props = Get-Content (Join-Path $repo "Directory.Build.props") -Raw
if ($props -notmatch "<Version>([^<]+)</Version>") { Write-Host "version not found"; exit 1 }
$version = $Matches[1]

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("hyperdrive-package-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path (Join-Path $stage "GameData\Hyperdrive") | Out-Null
robocopy (Join-Path $repo "GameData\Hyperdrive") (Join-Path $stage "GameData\Hyperdrive") /E /XD PluginData /XF .DS_Store /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Host "copy failed (robocopy $LASTEXITCODE)"; exit 1 }
Copy-Item README.md, CHANGELOG.md $stage
if (Test-Path LICENSE) { Copy-Item LICENSE $stage }

New-Item -ItemType Directory -Force -Path (Join-Path $repo "dist") | Out-Null
$out = Join-Path $repo ("dist\Hyperdrive-" + $version + ".zip")
if (Test-Path $out) { Remove-Item $out -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
# Entries written one by one with "/" separators: CreateFromDirectory in Windows PowerShell 5.1 keeps "\", which Mac and
# Linux unzip as file names containing backslashes.
$zip = [System.IO.Compression.ZipFile]::Open($out, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $name = $_.FullName.Substring($stage.Length).TrimStart('\').Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
Remove-Item $stage -Recurse -Force

$size = (Get-Item $out).Length / 1MB
Write-Host ("dist\Hyperdrive-" + $version + ".zip (" + $size.ToString("F1") + " MB)")
if (-not (Test-Path LICENSE)) { Write-Host "warning: no LICENSE file yet (to choose before any publication)" }
exit 0
