
# export_onnx.ps1
Write-Host "🔄 Exporting TensorFlow model to ONNX..."

Set-Location $PSScriptRoot\..\backend

.venv\Scripts\Activate.ps1

$model = "model/current_model.h5"
$output = "model/current_model.onnx"

if (-Not (Test-Path $model)) {
    Write-Host "❌ Model not found: $model"
    exit 1
}

python -m tf2onnx.convert --keras $model --output $output --opset 13

if (Test-Path $output) {
    Write-Host "✅ Model exported to $output"
} else {
    Write-Host "❌ Export failed."
}
