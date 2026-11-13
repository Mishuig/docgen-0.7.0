$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'DocumentGenerator.csproj'
$output  = Join-Path $PSScriptRoot 'publish\win-x64'
$raw = Get-Content $project -Raw
if ($raw -notmatch '<Version>([^<]+)</Version>') { throw 'Version not found in csproj' }
$version = $Matches[1].Trim()

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw '.NET 9 SDK is required. Install from https://dotnet.microsoft.com/download/dotnet/9.0'
}

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

Write-Host "Restoring..."
dotnet restore $project --runtime win-x64
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

Write-Host "Publishing self-contained folder (no single-file)..."
# Folder layout: most reliable for WPF + SQLite. Copy the whole folder.
dotnet publish $project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -p:PublishReadyToRun=true `
  -o $output

if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (-not (Test-Path (Join-Path $output 'DocumentGenerator.exe'))) {
  throw 'DocumentGenerator.exe was not produced.'
}

Copy-Item (Join-Path $PSScriptRoot 'App.ico') $output -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot 'create-shortcut.ps1') $output -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot 'Create-Shortcut.cmd') $output -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot 'Start-Docugen.cmd') $output -Force -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $PSScriptRoot 'samples')) {
  Copy-Item (Join-Path $PSScriptRoot 'samples') (Join-Path $output 'samples') -Recurse -Force
}

$zip = Join-Path $PSScriptRoot "publish\DocumentGenerator-$version-portable-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -Force

Write-Host ""
Write-Host "Portable folder: $output" -ForegroundColor Green
Write-Host "Portable ZIP:    $zip" -ForegroundColor Green
Write-Host "Copy the WHOLE folder (EXE + all DLLs). Do not run only the EXE from elsewhere." -ForegroundColor Cyan
