# Test module for the shell self-test and launch test. Never shipped.
#   hello [extra]  print its args and DESKKIT_* variables, exit 0      (oneshot)
#   sleep          sleep 120 s                                          (oneshot time limit)
#   window         hold Local\DeskKit.<id>, exit 0 on the stop event    (window, contract 7.2)
#   stubborn       hold the mutex, ignore the stop event                (stop protocol steps 2 and 3)
#   crash          take the mutex, write an ERROR line, exit 1          (crash notice)
param([string]$Mode = 'hello', [string]$Extra = '')
$id = if ($env:DESKKIT_MODULE_ID) { $env:DESKKIT_MODULE_ID } else { 'shelltest-window' }
Write-Output "fixture $id mode=$Mode"
Write-Output "arg1=$Mode"
Write-Output "arg2=$Extra"
Write-Output "DESKKIT_SHELL=$env:DESKKIT_SHELL"
Write-Output "DATA=$env:DESKKIT_DATA_DIR"
Write-Output "MODULE_DIR=$env:DESKKIT_MODULE_DIR"
switch ($Mode) {
  'hello' { exit 0 }
  'sleep' { Start-Sleep -Seconds 120; exit 0 }
}
$created = $false
$mutex = New-Object System.Threading.Mutex($false, "Local\DeskKit.$id", [ref]$created)
if (-not $created) {
  $show = $null
  try { $show = [System.Threading.EventWaitHandle]::OpenExisting("Local\DeskKit.$id.show"); [void]$show.Set() } catch { }
  Write-Output 'second copy: handed over'
  exit 0
}
if ($Mode -eq 'crash') { [Console]::Error.WriteLine('ERROR: fixture crashed on purpose'); exit 1 }
$show = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, "Local\DeskKit.$id.show")
$stop = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, "Local\DeskKit.$id.stop")
if ($Mode -eq 'stubborn') { Start-Sleep -Seconds 600; exit 0 }
while ($true) {
  $i = [System.Threading.WaitHandle]::WaitAny(@($show, $stop))
  if ($i -eq 1) { Write-Output 'stop received'; exit 0 }
  Write-Output 'show received'
}
