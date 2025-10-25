param([Parameter(Mandatory=$true)] [string] $ServiceName, [string] $NssmExe = "nssm.exe")
Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
& $NssmExe remove $ServiceName confirm
Write-Host "Service '$ServiceName' removed."
