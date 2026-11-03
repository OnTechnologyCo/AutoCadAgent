# Optional local Gemini install; GPT/Claude can use the user's existing CLI installations.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$toolsDir=Join-Path $root '.tools'
& npm.cmd install --prefix $toolsDir --no-audit --no-fund --save-exact '@google/gemini-cli@0.66.0'
if($LASTEXITCODE -ne 0){throw 'Gemini CLI install failed'}
Write-Host (Join-Path $toolsDir 'node_modules\.bin\gemini.cmd')
