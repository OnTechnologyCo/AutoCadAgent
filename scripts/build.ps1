param([string]$AutoCADSdkDir=$env:AUTOCAD_SDK_DIR)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=Split-Path -Parent $PSScriptRoot
if(-not $AutoCADSdkDir){$AutoCADSdkDir=Join-Path $root '.sdk\AutoCAD2024'}
Push-Location $root
try {
    if(-not (Test-Path 'web\vendor\xterm.js')) { throw 'web/vendor 자산이 없습니다.' }
    $out=Join-Path $root 'artifacts\app'
    & dotnet build src/AutoCadAgent.AutoCAD -c Release "-p:AutoCADSdkDir=$AutoCADSdkDir"
    if($LASTEXITCODE -ne 0){throw 'AutoCAD 빌드 실패'}
    & dotnet build tests/AutoCadAgent.BridgeChecks -c Release
    if($LASTEXITCODE -ne 0){throw 'Bridge 검사 빌드 실패'}
    & dotnet publish src/AutoCadAgent.Tui -c Release -r win-x64 --self-contained false -o $out
    if($LASTEXITCODE -ne 0){throw 'TUI 빌드 실패'}
    & dotnet publish src/AutoCadAgent.Cli -c Release -r win-x64 --self-contained false -o (Join-Path $out 'agent')
    if($LASTEXITCODE -ne 0){throw 'CLI 빌드 실패'}
    & tests/AutoCadAgent.BridgeChecks/bin/Release/net48/AutoCadAgent.BridgeChecks.exe --cli (Join-Path $out 'agent\cad-agent.exe')
    if($LASTEXITCODE -ne 0){throw 'Bridge 검사 실패'}
    New-Item (Join-Path $out 'cad') -ItemType Directory -Force | Out-Null
    Copy-Item 'src\AutoCadAgent.AutoCAD\bin\Release\net48\AutoCadAgent.AutoCAD.dll' (Join-Path $out 'cad') -Force
    Copy-Item 'licenses' $out -Recurse -Force
    Copy-Item 'THIRD_PARTY_NOTICES.md','README.md' $out -Force
    & dotnet run --project tests/AutoCadAgent.Checks -c Release
    if($LASTEXITCODE -ne 0){throw '검증 실패'}
    & node tests/source-check.cjs
    if($LASTEXITCODE -ne 0){throw 'Source contract checks failed'}
    Copy-Item 'docs' $out -Recurse -Force
    Write-Host "Build and checks completed: $out"
} finally {Pop-Location}
