
# check_env.ps1
Write-Host "🔍 Checking environment..."

# Python
$python = Get-Command python -ErrorAction SilentlyContinue
if ($python) {
    Write-Host "✅ Python found: $($python.Source)"
    python --version
} else {
    Write-Host "❌ Python not found."
}

# .NET SDK
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    Write-Host "✅ .NET SDK found: $($dotnet.Source)"
    dotnet --version
} else {
    Write-Host "❌ .NET SDK not found."
}

# Visual Studio
$vs = Get-Command devenv.exe -ErrorAction SilentlyContinue
if ($vs) {
    Write-Host "✅ Visual Studio found: $($vs.Source)"
} else {
    Write-Host "❌ Visual Studio not found in PATH."
}
