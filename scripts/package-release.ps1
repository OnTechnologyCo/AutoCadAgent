param([string]$Version='0.1.0',[string]$AutoCADSdkDir=$env:AUTOCAD_SDK_DIR)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$'){throw 'Invalid version'}
$root=Split-Path -Parent $PSScriptRoot
if(-not $AutoCADSdkDir){$AutoCADSdkDir=Join-Path $root '.sdk\AutoCAD2024'}
$name="AutoCadAgent-v$Version-win-x64"
$out=Join-Path $root "artifacts\release-stage\$name"
$release=Join-Path $root 'artifacts\releases'
if(Test-Path $out){throw 'Release stage already exists. Use a fresh version or archive the previous stage.'}
New-Item -ItemType Directory -Force $out,$release | Out-Null
Push-Location $root
try {
    & dotnet build src/AutoCadAgent.AutoCAD -c Release "-p:AutoCADSdkDir=$AutoCADSdkDir" "-p:Version=$Version"
    if($LASTEXITCODE -ne 0){throw 'CAD build failed'}
    & dotnet publish src/AutoCadAgent.Tui -c Release -r win-x64 --self-contained false "-p:Version=$Version" -o $out
    if($LASTEXITCODE -ne 0){throw 'TUI publish failed'}
    & dotnet publish src/AutoCadAgent.Cli -c Release -r win-x64 --self-contained false "-p:Version=$Version" -o (Join-Path $out 'agent')
    if($LASTEXITCODE -ne 0){throw 'CLI publish failed'}
    New-Item -ItemType Directory -Force (Join-Path $out 'cad'),(Join-Path $out 'scripts') | Out-Null
    Copy-Item 'src\AutoCadAgent.AutoCAD\bin\Release\net48\AutoCadAgent.AutoCAD.dll' (Join-Path $out 'cad')
    Copy-Item 'README.md','THIRD_PARTY_NOTICES.md','Start-AutoCadAgent.cmd' $out
    foreach($directory in 'docs','licenses','examples') {
        $source=Join-Path $root $directory
        foreach($file in Get-ChildItem $source -Recurse -File | Where-Object {$_.Extension -in '.md','.json','.txt' -and $_.Name -notlike '*.mcp-backup-*'}) {
            $relative=$file.FullName.Substring($source.Length+1)
            $destination=Join-Path (Join-Path $out $directory) $relative
            New-Item -ItemType Directory -Force (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    Copy-Item 'scripts\verify-netload.ps1' (Join-Path $out 'scripts')
    Get-ChildItem $out -Recurse -File -Filter '*.pdb' | Remove-Item -Force
    Get-ChildItem $out -File -Filter 'Microsoft.Web.WebView2.*.xml' | Remove-Item -Force
    & dotnet build tests/AutoCadAgent.BridgeChecks -c Release
    if($LASTEXITCODE -ne 0){throw 'Bridge check build failed'}
    & tests/AutoCadAgent.BridgeChecks/bin/Release/net48/AutoCadAgent.BridgeChecks.exe --cli (Join-Path $out 'agent\cad-agent.exe')
    if($LASTEXITCODE -ne 0){throw 'Bridge checks failed'}
    & dotnet run --project tests/AutoCadAgent.Checks -c Release
    if($LASTEXITCODE -ne 0){throw 'Core checks failed'}
    & node tests/source-check.cjs
    if($LASTEXITCODE -ne 0){throw 'Source checks failed'}
    foreach($required in 'AutoCadAgent.Tui.exe','agent\cad-agent.exe','cad\AutoCadAgent.AutoCAD.dll','Start-AutoCadAgent.cmd','web\vendor\xterm.js','licenses\AgentCLIHub-MIT.txt') {
        if(-not (Test-Path (Join-Path $out $required))){throw "Missing release file: $required"}
    }
    $forbidden=@(Get-ChildItem $out -Recurse -File | Where-Object {$_.Name -match '^(AcMgd|AcDbMgd|AcCoreMgd)\.dll$|\.pdb$|\.mcp-backup-|^settings\.json$|^\.env($|\.)'})
    if($forbidden.Count){throw 'Unexpected development file in release stage'}
    $zip=Join-Path $release ($name+'.zip')
    if(Test-Path $zip){throw 'Release ZIP already exists'}
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
    $sum=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+(Split-Path -Leaf $zip)
    [IO.File]::WriteAllText((Join-Path $release ($name+'.sha256')),$sum+"`n",[Text.UTF8Encoding]::new($false))
    Write-Host "Release package: $zip"
    Write-Host $sum
} finally {Pop-Location}
