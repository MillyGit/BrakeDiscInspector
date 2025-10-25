param(
    [Parameter(Mandatory=$true)] [string] $ServiceName,
    [Parameter(Mandatory=$true)] [string] $RepoDir,
    [Parameter(Mandatory=$true)] [string] $PythonExe,
    [string] $Host = "127.0.0.1",
    [int] $Port = 8000,
    [string] $NssmExe = "nssm.exe"
)

$ErrorActionPreference = "Stop"

$AppDir = Join-Path $RepoDir "backend"
$LogsDir = Join-Path $RepoDir "logs"
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

& $NssmExe install $ServiceName $PythonExe "-m uvicorn backend.app:app --host $Host --port $Port"
& $NssmExe set $ServiceName AppDirectory $RepoDir
& $NssmExe set $ServiceName AppStdout (Join-Path $LogsDir "stdout.log")
& $NssmExe set $ServiceName AppStderr (Join-Path $LogsDir "stderr.log")
& $NssmExe set $ServiceName Start SERVICE_AUTO_START

Write-Host "Service '$ServiceName' installed."
