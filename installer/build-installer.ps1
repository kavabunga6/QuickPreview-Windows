[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $PSScriptRoot
$payloadDirectory = Join-Path $PSScriptRoot 'payload'
$artifactsDirectory = Join-Path $projectDirectory 'artifacts'
$innoScript = Join-Path $PSScriptRoot 'QuickPreview.iss'
$innoCompilerCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$innoCompiler = $innoCompilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $innoCompiler) {
    throw 'Inno Setup 6 was not found. Install package JRSoftware.InnoSetup first.'
}

New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null

dotnet publish (Join-Path $projectDirectory 'QuickPreview.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $payloadDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$loaderPath = Join-Path $payloadDirectory 'WebView2Loader.dll'
if (-not (Test-Path -LiteralPath $loaderPath)) {
    throw 'Publish payload is missing WebView2Loader.dll; PDF preview would fail after installation.'
}

foreach ($requiredAsset in @('assets\model-viewer\viewer.html', 'assets\model-viewer\viewer.js',
        'assets\model-viewer\vendor\o3dv.min.js', 'assets\model-viewer\vendor\occt\occt-import-js.wasm',
        'assets\spreadsheet-viewer\viewer.html', 'assets\spreadsheet-viewer\viewer.js',
        'assets\spreadsheet-viewer\viewer.css', 'assets\licenses\ExcelDataReader-LICENSE.txt',
        'assets\licenses\ExcelNumberFormat-LICENSE.txt',
        'SUPPORTED_FORMATS.md', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadDirectory $requiredAsset))) {
        throw "Publish payload is missing required preview asset: $requiredAsset"
    }
}

& $innoCompiler $innoScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $artifactsDirectory 'QuickPreview-Setup.exe'
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw 'Inno Setup did not create QuickPreview-Setup.exe.'
}

Get-Item -LiteralPath $installerPath | Select-Object FullName, Length, LastWriteTime
