$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\DesktopTool.csproj'
$output = Join-Path $PSScriptRoot 'dist\DesktopTool-Reconstructed'
$data = Join-Path $output 'Data'
$dataBackup = Join-Path $PSScriptRoot '.portable-data-build-backup'

# Preserve zones, renamed titles and other portable user data across rebuilds.
if (Test-Path -LiteralPath $dataBackup) {
    Remove-Item -LiteralPath $dataBackup -Recurse -Force
}
if (Test-Path -LiteralPath $data) {
    Copy-Item -LiteralPath $data -Destination $dataBackup -Recurse -Force
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

New-Item -ItemType File -Path (Join-Path $output 'portable.txt') -Force | Out-Null
if (Test-Path -LiteralPath $dataBackup) {
    Copy-Item -LiteralPath $dataBackup -Destination $data -Recurse -Force
    Remove-Item -LiteralPath $dataBackup -Recurse -Force
}
Write-Host "Built: $output"
