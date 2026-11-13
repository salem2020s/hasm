. "$PSScriptRoot\common.ps1"
Assert-NotDevelopmentCluster
if (-not (Test-Path (Join-Path $DataDir 'PG_VERSION'))) { throw "No test cluster at $DataDir; run new-test-cluster.ps1 first." }
& "$PgBin\pg_ctl.exe" -D $DataDir status *> $null
if ($LASTEXITCODE -eq 0) { Write-Host 'Test cluster already running.'; return }
# Start-Process without -Wait (it would wait for the server's whole process tree) so the server does not inherit this shell's output handles.
Start-Process -FilePath "$PgBin\pg_ctl.exe" -ArgumentList @('-D', "`"$DataDir`"", '-l', "`"$(Join-Path $LogDir 'postgres.log')`"", '-w', '-t', '60', 'start') -WindowStyle Hidden | Out-Null
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Milliseconds 500; & "$PgBin\pg_isready.exe" -h 127.0.0.1 -p $TestPort *> $null } until ($LASTEXITCODE -eq 0 -or (Get-Date) -gt $deadline)
if ($LASTEXITCODE -ne 0) { throw 'Test cluster did not become ready; see logs.' }
Write-Host "Test cluster started on 127.0.0.1:$TestPort."
