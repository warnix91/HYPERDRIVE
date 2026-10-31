# Hyperdrive regression battery (Windows PowerShell), one command, ~10 minutes: builds, then four short AutoBench runs
# with the default settings (first load + flight, 3 scene cycles, crash of the heavy craft, VAB searches), then checks
# every Player.log against tools\regress\baseline_pc.txt (tools\regress\Check): unfinished run, failed subsystem,
# exception in Hyperdrive code, exception not seen before, temperatures moved. Exit code 0 = PASS.
#
# usage: powershell -ExecutionPolicy Bypass -File tools\regress.ps1 [-Update] [-KspRoot dir] [-Out dir]
#   -Update   (re)write the baseline from this run (only on a build known to be good)
param(
    [switch]$Update,
    [string]$KspRoot = "",
    [string]$Out = (Join-Path $env:TEMP "hyperdrive-regress")
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out | Out-Null

Write-Host "Building..."
dotnet build Hyperdrive.sln -c Release *> (Join-Path $Out "build.log")
if ($LASTEXITCODE -ne 0) { Select-String -Path (Join-Path $Out "build.log") -Pattern "error" | Select-Object -First 20; Write-Host "RESULT: FAIL (build)"; exit 1 }
dotnet build tools\regress\Check\Check.csproj -c Release *> (Join-Path $Out "check-build.log")
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $Out "check-build.log") -Tail 20; exit 1 }

$ErrorActionPreference = "Continue"
$runs = @(
    @{ Name = "load";   Cfg = "tools\bench\quick.cfg" },
    @{ Name = "cycles"; Cfg = "tools\regress\cycles3.cfg" },
    @{ Name = "crash";  Cfg = "tools\bench\crash.cfg" },
    @{ Name = "editor"; Cfg = "tools\bench\editor_search.cfg" }
)
foreach ($run in $runs) {
    Write-Host ("Run " + $run.Name + " (" + $run.Cfg + ")...")
    $benchArgs = @("-ExecutionPolicy", "Bypass", "-File", "tools\bench.ps1", "-Out", (Join-Path $Out $run.Name), "-Timeout", "1200",
                   "-UserCfg", "tools\regress\defaults.cfg", "-BenchCfg", $run.Cfg)
    if ($KspRoot -ne "") { $benchArgs += @("-KspRoot", $KspRoot) }
    & powershell @benchArgs *> (Join-Path $Out ($run.Name + ".bench.txt"))
    Get-Content (Join-Path $Out ($run.Name + ".bench.txt")) -Tail 1
}

$checkArgs = @("tools\regress\Check\bin\Release\net6.0\Check.dll", "tools\regress\baseline_pc.txt")
if ($Update) { $checkArgs += "--update" }
foreach ($run in $runs) { $checkArgs += ($run.Name + "=" + (Join-Path $Out $run.Name)) }
& dotnet @checkArgs
exit $LASTEXITCODE
