
# setup_dev.ps1
Write-Host "🚀 Setting up BrakeDiscInspector development environment..."

Set-Location $PSScriptRoot\..\backend

# Crear venv si no existe
if (-Not (Test-Path ".venv")) {
    python -m venv .venv
    Write-Host "✅ Virtual environment created."
}

# Activar venv
.venv\Scripts\Activate.ps1

# Instalar requirements
pip install --upgrade pip
pip install -r requirements.txt

# Crear carpeta de logs
if (-Not (Test-Path "logs")) {
    New-Item -ItemType Directory -Path "logs" | Out-Null
}

Write-Host "✅ Backend dependencies installed and ready."
