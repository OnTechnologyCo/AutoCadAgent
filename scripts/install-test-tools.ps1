$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$toolsDir=Join-Path $root '.tools'
& npm.cmd install --prefix $toolsDir --no-audit --no-fund --save-dev --save-exact 'playwright-core@1.64.1'
if($LASTEXITCODE -ne 0){throw 'UI test dependency install failed'}
$env:PLAYWRIGHT_BROWSERS_PATH=Join-Path $toolsDir 'browsers'
& node.exe (Join-Path $toolsDir 'node_modules\playwright-core\cli.js') install chromium
if($LASTEXITCODE -ne 0){throw 'UI test browser install failed'}
