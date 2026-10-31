# Face-to-face runs of a job done both by Hyperdrive and KSPCommunityFixes (needs KSPCF installed): the same short
# AutoBench run with Compatibility PREFER <job> = ours, then = theirs, and the numbers side by side. Use it for jobs
# without a neutral measure in play (FasterEditorPartList) or to check what an install learned.
#
# usage: powershell -ExecutionPolicy Bypass -File tools\overlaps.ps1 -Job <job> [-Runs 1] [-KspRoot dir]
#   jobs: FlightIntegratorPerf, OnDemandPartBuoyancy (heavy craft, 40 s on the pad), FasterEditorPartList (VAB searches),
#         SceneLoadSpeedBoost (first load + flight: game load time)
param(
    [Parameter(Mandatory = $true)][string]$Job,
    [int]$Runs = 1,
    [string]$KspRoot = ""
)
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$out = Join-Path $env:TEMP ("hyperdrive-overlaps\" + $Job)
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null
switch ($Job) {
    "FasterEditorPartList" { $bench = "tools\bench\editor_search.cfg"; $pattern = "AutoBench EDITOR|AutoBench DONE" }
    "SceneLoadSpeedBoost"  { $bench = "tools\bench\quick.cfg"; $pattern = "AutoBench: scene SPACECENTER ready|Overlaps:" }
    default                { $bench = "tools\regress\flight40.cfg"; $pattern = "AutoBench REPORT|Timeline ms/frame" }
}
for ($i = 1; $i -le $Runs; $i++) {
    foreach ($side in @("ours", "theirs")) {
        $cfg = Join-Path $out ($side + ".cfg")
        Set-Content -Path $cfg -Encoding ASCII -Value ("HYPERDRIVE`n{`n`tCompatibility`n`t{`n`t`tlearnOverlaps = false`n`t`tPREFER`n`t`t{`n`t`t`t" + $Job + " = " + $side + "`n`t`t}`n`t}`n}`n")
        $benchArgs = @("-ExecutionPolicy", "Bypass", "-File", "tools\bench.ps1", "-Out", (Join-Path $out ($side + $i)), "-Timeout", "1200", "-UserCfg", $cfg, "-BenchCfg", $bench)
        if ($KspRoot -ne "") { $benchArgs += @("-KspRoot", $KspRoot) }
        & powershell @benchArgs *> (Join-Path $out ($side + $i + ".bench.txt"))
        Write-Host ("== " + $Job + " = " + $side + " (run " + $i + ")")
        Select-String -Path (Join-Path (Join-Path $out ($side + $i)) "Player.log") -Pattern $pattern | ForEach-Object { $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) }
    }
}
