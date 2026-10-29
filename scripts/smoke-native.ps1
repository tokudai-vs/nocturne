# Pre-handover smoke test for the v4 native build (CLAUDE.md, "v4 smoke test"). Launches a build against an isolated
# profile (NOCTURNE_DATA_DIR), samples memory and I/O every 5 s, closes it with WM_CLOSE (no keyboard or mouse input),
# and checks its log. Exit code 0 = PASS. Prints no tokens or URLs.
#
#   powershell -ExecutionPolicy Bypass -File scripts\smoke-native.ps1 -Exe bin\native-win\Nocturne.exe -DataDir <copy>
#
# DataDir: a copy of a real profile (settings, device.json, database) with Trakt disconnected in the copy, so the run
# cannot pull, scrobble or write watched state. Copy the database while Nocturne is closed.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$DataDir,
    [int]$Minutes = 6,
    [int]$MaxPrivateMB = 2500
)
$log = Join-Path $DataDir 'nocturne.log'
$startLine = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }
$env:NOCTURNE_DATA_DIR = (Resolve-Path $DataDir).Path
$p = Start-Process -FilePath $Exe -PassThru
$t0 = Get-Date; $deadline = $t0.AddMinutes($Minutes)
$peakPriv = 0; $maxIo = 0; $lastIo = $null; $timeline = @()
while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
    Start-Sleep 5
    try { $p.Refresh(); $priv = $p.PrivateMemorySize64 / 1MB } catch { break }
    $io = Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)" -ErrorAction SilentlyContinue
    $rate = if ($io -and $lastIo) { (($io.ReadTransferCount + $io.WriteTransferCount) - ($lastIo.ReadTransferCount + $lastIo.WriteTransferCount)) / 5 / 1MB } else { 0 }
    if ($io) { $lastIo = $io }
    if ($priv -gt $peakPriv) { $peakPriv = $priv }; if ($rate -gt $maxIo) { $maxIo = $rate }
    $s = [int]((Get-Date) - $t0).TotalSeconds
    if ($s % 30 -lt 5) { $timeline += ('  t+{0,4}s  private {1,6:N0} MB  I/O {2,6:N1} MB/s' -f $s, $priv, $rate) }
}
$failures = @()
if ($p.HasExited) { $failures += "exited on its own after $([int]((Get-Date) - $t0).TotalSeconds) s (exit code $($p.ExitCode))" }
else {
    $p.CloseMainWindow() | Out-Null
    if (-not $p.WaitForExit(90000)) { $p.Kill(); $failures += 'did not close within 90 s of WM_CLOSE' }
}
$lines = @(Get-Content $log | Select-Object -Skip $startLine)
if (-not ($lines -match '\[Startup\] first render')) { $failures += 'no first render logged' }
if ($lines -match '\[Startup\] mpv: NOT FOUND') { $failures += 'mpv not found' }
$errors = @($lines | Where-Object { $_ -match '\[ERROR\]' })
if ($errors.Count) { $failures += "$($errors.Count) ERROR line(s)" }
if ($peakPriv -gt $MaxPrivateMB) { $failures += ('peak private memory {0:N0} MB over {1:N0} MB' -f $peakPriv, $MaxPrivateMB) }
if ($lines -match 'Measuring drift') { $failures += 'drift measured during the run' }

'peak private {0:N0} MB, max I/O {1:N1} MB/s' -f $peakPriv, $maxIo
$timeline
'-- [Startup], sync, drift'
$lines | Where-Object { $_ -match '\[Startup\]|[Dd]rift|Multi-server sync|WAL checkpoint after closing' } | ForEach-Object { '  ' + $_ }
'-- ERROR / WARN (merge refusals and trailer lookups left out)'
$lines | Where-Object { $_ -match '\[(ERROR|WARN)\]' -and $_ -notmatch 'Merge refused|merge refusals|TrailerService' } | ForEach-Object { '  ' + $_ }
if ($failures.Count) { "FAIL: $($failures -join '; ')"; exit 1 }
'PASS'
