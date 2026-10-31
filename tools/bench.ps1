# Hyperdrive AutoBench runner (Windows PowerShell): arms the AutoBench, launches KSP, waits for "AutoBench DONE" in
# Player.log (or KSP exit / timeout), copies the logs and the 10 s screenshot, then disarms.
# Never starts while KSP is running and only stops the instance it started.
#
# usage: powershell -ExecutionPolicy Bypass -File tools\bench.ps1 -Out <dir> [-Timeout 1200] [-UserCfg file] [-BenchCfg file] [-KspRoot dir]
#   -UserCfg   copied to GameData\Hyperdrive\PluginData\Hyperdrive.user.cfg for this run (e.g. SUBSYSTEMS switches)
#   -BenchCfg  AUTOBENCH { craft = ...  flightSeconds = ... }; default: Kerbal X, 120 s, staging every 10 s
#   -KspArgs   extra KSP command line for this run, e.g. "-force-gfx-jobs legacy" (Unity graphics jobs, DX11)
#   -Session   state written to PluginData\Session.txt before the launch, e.g. "running in=FLIGHT" (prudent start test)
#   -MaxLogMB / -StallSeconds  stop KSP if Player.log floods past this size, or stops growing for this long
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Timeout = 1200,
    [string]$UserCfg = "",
    [string]$BenchCfg = "",
    [string]$KspRoot = "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program",
    [int]$MaxLogMB = 1024,
    [int]$StallSeconds = 600,
    [string]$KspArgs = "",
    [string]$Session = ""
)

$playerLog = Join-Path $env:USERPROFILE "AppData\LocalLow\Squad\Kerbal Space Program\Player.log"
$exe = Join-Path $KspRoot "KSP_x64.exe"
if (-not (Test-Path $exe)) { Write-Error "KSP_x64.exe not found in $KspRoot (use -KspRoot)"; exit 1 }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$waited = 0
while (Get-Process -Name "KSP_x64" -ErrorAction SilentlyContinue) {
    Start-Sleep -Seconds 10; $waited += 10
    if ($waited -gt 1800) { Write-Output "SKIPPED: another KSP is running"; exit 3 }
}

$pd = Join-Path $KspRoot "GameData\Hyperdrive\PluginData"
New-Item -ItemType Directory -Force -Path $pd | Out-Null
$trigger = Join-Path $pd "autobench.cfg"
if ($BenchCfg -ne "") { Copy-Item $BenchCfg $trigger -Force }
else {
    "AUTOBENCH`n{`n`tcraft = Kerbal X`n`tflightSeconds = 120`n`tstageIntervalSeconds = 10`n`tpauseAtSeconds = 60`n`tpauseDurationSeconds = 4`n`tquitWhenDone = true`n}`n" |
        Set-Content -Path $trigger -Encoding ASCII
}
# The player's own overrides are set aside for the run (benchmarks use known settings) and put back after.
$userCfgPath = Join-Path $pd "Hyperdrive.user.cfg"
$savedUserCfg = Join-Path $pd "Hyperdrive.user.cfg.player"
if ((Test-Path $userCfgPath) -and -not (Test-Path $savedUserCfg)) { Move-Item $userCfgPath $savedUserCfg -Force }
if ($UserCfg -ne "") { Copy-Item $UserCfg $userCfgPath -Force }
# A run killed by its timeout would make the next start a prudent one (PrudentStart): benches always start normally.
Remove-Item (Join-Path $pd "Session.txt") -Force -ErrorAction SilentlyContinue
if ($Session -ne "") { Set-Content -Path (Join-Path $pd "Session.txt") -Value $Session -NoNewline -Encoding ASCII }

# Reads what Player.log gained since the last call (shared read: Unity keeps the file open for writing).
$script:logPos = 0L
function Read-NewLog {
    try {
        $fs = [System.IO.File]::Open($playerLog, 'Open', 'Read', 'ReadWrite')
        try {
            if ($fs.Length -lt $script:logPos) { $script:logPos = 0L }
            $count = [int][Math]::Min($fs.Length - $script:logPos, 64MB)
            if ($count -le 0) { return "" }
            [void]$fs.Seek($script:logPos, 'Begin')
            $buffer = New-Object byte[] $count
            $read = $fs.Read($buffer, 0, $count)
            $script:logPos += $read
            return [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
        } finally { $fs.Dispose() }
    } catch { return "" }
}

# Copies at most $maxBytes of a file (a flooded Player.log can reach gigabytes).
function Copy-Head([string]$src, [string]$dstDir, [long]$maxBytes) {
    if (-not (Test-Path $src)) { return }
    $dst = Join-Path $dstDir (Split-Path $src -Leaf)
    $in = [System.IO.File]::Open($src, 'Open', 'Read', 'ReadWrite')
    try {
        $outStream = [System.IO.File]::Create($dst)
        try {
            $buffer = New-Object byte[] 1MB; $left = $maxBytes
            while ($left -gt 0) {
                $n = $in.Read($buffer, 0, [int][Math]::Min($buffer.Length, $left))
                if ($n -le 0) { break }
                $outStream.Write($buffer, 0, $n); $left -= $n
            }
        } finally { $outStream.Dispose() }
    } finally { $in.Dispose() }
}

$start = Get-Date
if ($KspArgs) { $proc = Start-Process -FilePath $exe -WorkingDirectory $KspRoot -ArgumentList $KspArgs -PassThru }
else { $proc = Start-Process -FilePath $exe -WorkingDirectory $KspRoot -PassThru }
Start-Sleep -Seconds 20
$lastSize = -1L; $lastGrowth = Get-Date
while ($true) {
    Start-Sleep -Seconds 5
    $elapsed = [int]((Get-Date) - $start).TotalSeconds
    # Only trust a Player.log written by this run (a stale log from the previous run also says "AutoBench DONE").
    $fresh = (Test-Path $playerLog) -and ((Get-Item $playerLog).LastWriteTime -gt $start)
    if ($fresh -and (Read-NewLog).Contains("AutoBench DONE")) {
        Write-Output "DONE after ${elapsed}s"; Start-Sleep -Seconds 8; break
    }
    if ($proc.HasExited) { Write-Output "KSP_EXITED after ${elapsed}s"; break }
    if ($elapsed -gt $Timeout) { Write-Output "TIMEOUT after ${elapsed}s"; break }
    if ($fresh) {
        # Watchdogs: a log flood (a mod spamming every frame) or a game that stopped logging altogether.
        $size = (Get-Item $playerLog).Length
        if ($size -gt $MaxLogMB * 1MB) { Write-Output "LOG_FLOOD after ${elapsed}s (Player.log over $MaxLogMB MB)"; break }
        if ($size -ne $lastSize) { $lastSize = $size; $lastGrowth = Get-Date }
        elseif (((Get-Date) - $lastGrowth).TotalSeconds -gt $StallSeconds) { Write-Output "STALL after ${elapsed}s (no log output for $StallSeconds s)"; break }
    }
}
# Ask the window to close first (KSP then flushes KSP.log); Stop-Process alone is an immediate kill on Windows.
if (-not $proc.HasExited) { [void]$proc.CloseMainWindow(); $proc.WaitForExit(15000) | Out-Null }
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 3 }

Copy-Head (Join-Path $KspRoot "KSP.log") $Out 256MB
Copy-Head $playerLog $Out 256MB
Get-ChildItem $pd -Filter *.png -ErrorAction SilentlyContinue | ForEach-Object { Move-Item $_.FullName $Out -Force }
Remove-Item $trigger, $userCfgPath -ErrorAction SilentlyContinue
if (Test-Path $savedUserCfg) { Move-Item $savedUserCfg $userCfgPath -Force }
if (-not (Get-ChildItem $pd -ErrorAction SilentlyContinue)) { Remove-Item $pd -ErrorAction SilentlyContinue }
Select-String -Path (Join-Path $Out "Player.log") -Pattern "Boehm:|AutoBench REPORT|===== LoadReport|By category|FastLoader: [0-9]" |
    ForEach-Object { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) }
