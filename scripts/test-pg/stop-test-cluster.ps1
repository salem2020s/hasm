. "$PSScriptRoot\common.ps1"
Assert-NotDevelopmentCluster
Start-Process -FilePath "$PgBin\pg_ctl.exe" -ArgumentList @('-D', "`"$DataDir`"", '-m', 'fast', '-w', 'stop') -WindowStyle Hidden | Out-Null
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Milliseconds 500; & "$PgBin\pg_ctl.exe" -D $DataDir status *> $null } until ($LASTEXITCODE -ne 0 -or (Get-Date) -gt $deadline)
if ($LASTEXITCODE -eq 0) { throw 'Test cluster did not stop in time.' }
Write-Host 'Test cluster stopped.'
