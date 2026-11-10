# Test module: print every argument it receives, one per line, so tests can check quoting and order.
# No param block, so every argument lands in $args unchanged. Never shipped.
$i = 0
foreach ($a in $args) { Write-Output "argv[$i]=<$a>"; $i++ }
Write-Output "argc=$i"
