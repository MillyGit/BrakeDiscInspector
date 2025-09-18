
# run_gui.ps1
Write-Host "▶ Opening GUI solution in Visual Studio..."

Set-Location $PSScriptRoot\..\gui
Start-Process devenv.exe "BrakeDiscInspector_GUI_ROI.sln"
