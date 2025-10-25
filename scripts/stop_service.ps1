param([Parameter(Mandatory=$true)] [string] $ServiceName)
Stop-Service -Name $ServiceName
Get-Service -Name $ServiceName
