# Time from KSP's process start to its main menu, measured from outside (Player.log), so it works the same with or
# without Hyperdrive installed: launches KSP, waits for "Scene Change : From LOADING to MAINMENU", optionally waits for
# Hyperdrive's LZ4 copies of the expansions (written once, at the main menu), then stops the instance it started.
# Never starts while KSP is running. Appends "label<TAB>run<TAB>seconds" to -Csv.
# usage: powershell -ExecutionPolicy Bypass -File tools\menu_time.ps1 -KspRoot <dir> -Label <name> -Runs 3 -Csv <file>
param(
    [Parameter(Mandatory = $true)][string]$KspRoot,
    [Parameter(Mandatory = $true)][string]$Label,
    [int]$Runs = 3,
    [string]$Csv = "",
    [int]$Timeout = 900,
    [int]$AfterMenu = 5
)

$playerLog = Join-Path $env:USERPROFILE "AppData\LocalLow\Squad\Kerbal Space Program\Player.log"
$exe = Join-Path $KspRoot "KSP_x64.exe"
if (-not (Test-Path $exe)) { Write-Error "KSP_x64.exe not found in $KspRoot"; exit 1 }
$marker = "Scene Change : From LOADING to MAINMENU"
$hd = Join-Path $KspRoot "GameData\Hyperdrive"

for ($run = 1; $run -le $Runs; $run++) {
    if (Get-Process -Name "KSP_x64" -ErrorAction SilentlyContinue) { Write-Output "SKIPPED: another KSP is running"; exit 3 }
    # A stopped run leaves Hyperdrive's session marker: the next start would be a prudent one. Benches start normally.
    Remove-Item (Join-Path $hd "PluginData\Session.txt") -Force -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $exe -WorkingDirectory $KspRoot -PassThru
    $t0 = $p.StartTime
    Start-Sleep -Milliseconds 1500   # Unity moves the previous Player.log aside first
    $pos = 0; $menu = $null; $buf = ""
    # Hyperdrive writes LZ4 copies of the expansion bundles at the main menu, once: wait for the missing ones.
    $cache = Join-Path $hd "PluginData\BundleCache"
    $bundles = @(Get-ChildItem (Join-Path $KspRoot "GameData\SquadExpansion") -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq "AssetBundles" }).Count
    $have = if (Test-Path $cache) { @(Get-ChildItem $cache -File -Filter "*.lz4").Count } else { 0 }
    $missing = $bundles - $have
    $lz4Done = 0; $lz4Wanted = (Test-Path $hd) -and $missing -gt 0; $menuAt = $null
    while ($true) {
        if ($p.HasExited) { break }
        if (((Get-Date) - $t0).TotalSeconds -gt $Timeout) { break }
        if (Test-Path $playerLog) {
            $fs = [System.IO.File]::Open($playerLog, 'Open', 'Read', 'ReadWrite')
            try {
                if ($fs.Length -lt $pos) { $pos = 0 }
                $fs.Seek($pos, 'Begin') | Out-Null
                $sr = New-Object System.IO.StreamReader($fs)
                $chunk = $sr.ReadToEnd(); $pos = $fs.Position
            } finally { $fs.Dispose() }
            $text = $buf + $chunk
            $cut = $text.LastIndexOf("`n"); if ($cut -ge 0) { $buf = $text.Substring($cut + 1); $text = $text.Substring(0, $cut) } else { $buf = $text; $text = "" }
            if ($null -eq $menu -and $text.Contains($marker)) { $menu = (Get-Date) - $t0; $menuAt = Get-Date }
            $lz4Done += ([regex]::Matches($text, "AsyncExpansions: (LZ4 copy of [^\r\n]*(written in|not kept|failed|not started)|no LZ4 copy of)")).Count
        }
        if ($null -ne $menu) {
            $waitFor = if ($lz4Wanted -and $lz4Done -lt $missing) { 240 } else { $AfterMenu }
            if (((Get-Date) - $menuAt).TotalSeconds -ge $waitFor) { break }
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(30000) | Out-Null }
    $s = if ($null -ne $menu) { $menu.TotalSeconds.ToString("F1", [Globalization.CultureInfo]::InvariantCulture) } else { "FAILED" }
    Write-Output ("{0} run {1}: {2} s to main menu{3}" -f $Label, $run, $s, $(if ($lz4Wanted) { " (LZ4 copies written: $lz4Done of $missing)" } else { "" }))
    if ($Csv -ne "") { Add-Content -Path $Csv -Value ("{0}`t{1}`t{2}" -f $Label, $run, $s) }
    Start-Sleep -Seconds 3
}
exit 0
