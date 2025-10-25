param([Parameter(Mandatory=$true)] [string] $ServiceName)
Start-Service -Name $ServiceName
Get-Service -Name $ServiceName
