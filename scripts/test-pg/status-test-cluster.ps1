. "$PSScriptRoot\common.ps1"
Assert-NotDevelopmentCluster
& "$PgBin\pg_ctl.exe" -D $DataDir status
$l = Get-NetTCPConnection -State Listen -LocalPort $TestPort -ErrorAction SilentlyContinue
if ($l) { "Listening on: " + (($l | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }) -join ', ') } else { "Nothing listening on port $TestPort." }
