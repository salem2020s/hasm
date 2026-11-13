# Shared settings for the disposable HASM test PostgreSQL cluster. No secrets live here.
# The permanent development cluster (127.0.0.1:5432) is never referenced, started, stopped or modified by these scripts.
$ErrorActionPreference = 'Stop'
$script:TestPort    = 5433                      # fixed on purpose; the integration-test gate also requires it
$script:TestRoot    = if ($env:HASM_TEST_ROOT) { $env:HASM_TEST_ROOT } else { 'C:\HasmTest' }
$script:PgBin       = if ($env:HASM_PG_BIN) { $env:HASM_PG_BIN } else { 'C:\Program Files\PostgreSQL\18\bin' }
$script:DataDir     = Join-Path $script:TestRoot 'pgdata'
$script:LogDir      = Join-Path $script:TestRoot 'logs'
$script:SecretsDir  = Join-Path $script:TestRoot 'secrets'
$script:EnvFile     = Join-Path $script:SecretsDir 'test-env.ps1'
$script:SuperFile   = Join-Path $script:SecretsDir 'superuser.txt'
$script:MarkerDb    = 'hasm_test_cluster_identity'

function Assert-NotDevelopmentCluster {
    if ($script:DataDir -match 'PostgreSQL\\d+\data') { throw 'Refusing: the data directory looks like a permanent PostgreSQL installation.' }
}
function Protect-Path([string]$Path) {
    # Owner-only ACL: remove inheritance and every other entry, grant full control to the current user by SID only.
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $perm = if ((Get-Item $Path).PSIsContainer) { "*${sid}:(OI)(CI)F" } else { "*${sid}:F" }
    & icacls $Path /inheritance:r | Out-Null
    & icacls $Path /grant:r $perm | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $Path" }
    foreach ($p in 'Authenticated Users', 'Users', 'Everyone', 'BUILTIN\Users') { & icacls $Path /remove:g $p *> $null }
}
function New-Secret { -join ((1..24) | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) }) }
function New-StrongSecret {
    $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Fill($b); ([BitConverter]::ToString($b) -replace '-', '').ToLower()
}
