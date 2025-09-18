
# run_backend.ps1
Write-Host "▶ Starting backend..."

Set-Location $PSScriptRoot\..\backend

.venv\Scripts\Activate.ps1
python app.py
