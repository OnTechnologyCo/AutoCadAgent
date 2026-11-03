param(
    [string]$Pipe,
    [switch]$DrawTest,
    [string]$OutputDirectory
)
# Run under the SAME Windows user as AutoCAD. DrawTest requires an empty model space.
# It leaves test geometry in that drawing and saves a uniquely named DWG copy.
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$root=Split-Path -Parent $PSScriptRoot
$cad=Join-Path $root 'agent\cad-agent.exe'
if(-not (Test-Path $cad)){$cad=Join-Path $root 'artifacts\app\agent\cad-agent.exe'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts\validation\netload-'+[guid]::NewGuid().ToString('N'))}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$script:report=[ordered]@{timeUtc=[DateTime]::UtcNow.ToString('O');status='running';mode=$(if($DrawTest){'native-drawing'}else{'read-only'});checks=@();responses=@();nativeReopen='manual check required'}
$script:client=[guid]::NewGuid().ToString()
$script:document=$null
function Assert-Check([bool]$Condition,[string]$Name){
    $script:report.checks+=@{name=$Name;passed=$Condition}
    if(-not $Condition){throw "FAIL: $Name"}
    Write-Host "PASS: $Name"
}
function Invoke-Agent([string[]]$Command,[switch]$FailureExpected){
    $raw=(& $cad @Command | Out-String)
    $code=$LASTEXITCODE
    try {$response=$raw | ConvertFrom-Json} catch {throw "Invalid CLI JSON: $raw"}
    $script:report.responses+=@{command=$Command;response=$response;exitCode=$code}
    if($FailureExpected){if($code -eq 0 -or $response.ok -eq $true){throw 'Expected rejection but request succeeded'}}
    elseif($code -ne 0 -or $response.ok -ne $true){throw ('CAD request failed. Do not replay mutations. Response: '+$raw)}
    return $response
}
function Invoke-Tool([string]$Name,[hashtable]$Parameters=@{},[string]$RequestId=([guid]::NewGuid().ToString()),[switch]$FailureExpected,[string]$Client=$script:client,[string]$Document=$script:document){
    $file=Join-Path $OutputDirectory ($RequestId+'-args.json')
    [IO.File]::WriteAllText($file,($Parameters|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
    return Invoke-Agent -Command @('--pipe',$Pipe,'--document',$Document,'--client',$Client,'--id',$RequestId,'call',$Name,'--file',$file) -FailureExpected:$FailureExpected
}
function Total-Entities($Response){
    if($null -ne $Response.result.Data.count){return [int]$Response.result.Data.count}
    $first=($Response.result.Summary -split '\r?\n')[0]
    $match=[regex]::Match($first,'\d+\s*$')
    if(-not $match.Success){throw 'Could not read model-space entity count'}
    return [int]$match.Value.Trim()
}
$exitCode=0
try {
    if(-not (Test-Path $cad)){throw 'Build the project first: scripts\build.ps1'}
    if(-not $Pipe){
        $discovered=(& $cad discover | Out-String | ConvertFrom-Json)
        if($LASTEXITCODE -ne 0){throw 'CLI discovery failed'}
        $endpoints=@($discovered | Where-Object {$_ -ne $null})
        if($endpoints.Count -eq 0){$script:report.status='blocked-no-cad';throw 'No AutoCAD bridge. Open AutoCAD 2024, NETLOAD artifacts\app\cad\AutoCadAgent.AutoCAD.dll, then rerun.'}
        if($endpoints.Count -ne 1){throw 'Multiple AutoCAD instances. Supply -Pipe from cad-agent discover.'}
        $Pipe=$endpoints[0].Pipe
    }
    $status=Invoke-Agent -Command @('--pipe',$Pipe,'doctor')
    $script:document=$status.document
    Assert-Check (-not [string]::IsNullOrWhiteSpace($script:document)) 'Active drawing token returned'
    $catalog=Invoke-Agent -Command @('--pipe',$Pipe,'tools')
    Assert-Check (@($catalog.tools).Count -eq 122 -and $status.tools -eq 122) 'All 122 tools registered'
    Assert-Check (@($catalog.tools | Where-Object name -eq 'draw.commit').Count -eq 1) 'Commit tool registered once'
    $before=Invoke-Tool 'drawing.summary_full'
    if($DrawTest){
        Assert-Check ((Total-Entities $before) -eq 0) 'Blank model space required before any drawing changes'
        $null=Invoke-Tool 'drawing.summary_full' -Document 'invalid-test-document' -FailureExpected
        Assert-Check $true 'Stale document token rejected'
        $null=Invoke-Tool 'draw.circle' @{} -FailureExpected
        Assert-Check $true 'Missing tool parameters rejected'
        $null=Invoke-Tool 'draw.start_session' @{description='NETLOAD integration test'}
        $null=Invoke-Tool 'draw.rectangle' @{x1=0;y1=0;x2=200;y2=120;layer='CADAGENT_TEST_OUTLINE';color=7}
        $null=Invoke-Tool 'draw.circle' @{cx=100;cy=60;r=25;layer='CADAGENT_TEST_OUTLINE'}
        $null=Invoke-Tool 'draw.hatch_solid' @{boundary=@(@(230,0),@(280,0),@(280,50),@(230,50));layer='CADAGENT_TEST_HATCH'}
        $null=Invoke-Tool 'draw.dim_linear' @{x1=0;y1=0;x2=200;y2=0;base_x=100;base_y=-30;horizontal=$true;layer='CADAGENT_TEST_DIM'}
        Assert-Check ((Total-Entities (Invoke-Tool 'drawing.summary_full')) -eq 0) 'Staged shapes have not entered the drawing'
        $null=Invoke-Tool 'draw.commit' @{x=0;y=0} -Client ([guid]::NewGuid().ToString()) -FailureExpected
        Assert-Check $true 'Another CLI session cannot commit this buffer'
        $commitId=[guid]::NewGuid().ToString()
        $committed=Invoke-Tool 'draw.commit' @{x=1000;y=2000} -RequestId $commitId
        $handles=@($committed.result.Data.handles)
        Assert-Check ($handles.Count -eq 5 -and @($handles|Select-Object -Unique).Count -eq 5) 'Commit returns five distinct real entity handles'
        $replayed=Invoke-Tool 'draw.commit' @{x=1000;y=2000} -RequestId $commitId
        Assert-Check ((@($replayed.result.Data.handles) -join ',') -eq ($handles -join ',')) 'Duplicate commit ID returns the same handles'
        Assert-Check ((Total-Entities (Invoke-Tool 'drawing.summary_full')) -eq 5) 'Duplicate commit created no extra entities'
        $details=@($handles | ForEach-Object {(Invoke-Tool 'drawing.entity_detail' @{handle=$_}).result.Summary})
        Assert-Check (@($details | Where-Object {$_ -match 'type=Polyline' -and $_ -match 'area=24000\.00' -and $_ -match 'v0=\(1000\.0,2000\.0\)'}).Count -eq 1) 'Rectangle size and WCS translation verified'
        Assert-Check (@($details | Where-Object {$_ -match 'type=Circle' -and $_ -match 'radius=25\.00' -and $_ -match 'center=\(1100\.0,2060\.0\)'}).Count -eq 1) 'Circle radius and center verified'
        Assert-Check (@($details | Where-Object {$_ -match 'type=Hatch'}).Count -eq 1) 'Real hatch entity created'
        Assert-Check (@($details | Where-Object {$_ -match 'type=RotatedDimension'}).Count -eq 1) 'Real dimension entity created'
        $circleHandle=$handles[1]
        $circle=(Invoke-Tool 'drawing.entity_detail' @{handle=$circleHandle}).result.Data
        Assert-Check ($circle.geometryAvailable -and [Math]::Abs($circle.radius-25) -lt 1e-9) 'Structured circle measurement available'
        $null=Invoke-Tool 'draw.move' @{handles=@($circleHandle,$circleHandle);dx=1;dy=1} -FailureExpected
        $null=Invoke-Tool 'draw.move' @{handles=@($circleHandle,'0');dx=1;dy=1} -FailureExpected
        $unchanged=(Invoke-Tool 'drawing.entity_detail' @{handle=$circleHandle}).result.Data
        Assert-Check ([Math]::Abs($unchanged.center[0]-1100) -lt 1e-9 -and [Math]::Abs($unchanged.center[1]-2060) -lt 1e-9) 'Invalid handle groups made no partial edit'
        $null=Invoke-Tool 'draw.move' @{handles=@($circleHandle);dx=0.123456789;dy=-0.987654321}
        $moved=(Invoke-Tool 'drawing.entity_detail' @{handle=$circleHandle}).result.Data
        Assert-Check ([Math]::Abs($moved.center[0]-1100.123456789) -lt 1e-9 -and [Math]::Abs($moved.center[1]-2059.012345679) -lt 1e-9) 'Edit then exact numeric reinspection passed'
        $dwg=Join-Path $OutputDirectory ('netload-test-'+[guid]::NewGuid().ToString('N')+'.dwg')
        $null=Invoke-Tool 'drawing.save_copy' @{path=$dwg}
        Assert-Check ((Test-Path $dwg) -and (Get-Item $dwg).Length -gt 0) 'DWG copy exists and is nonempty'
        $hash=(Get-FileHash $dwg -Algorithm SHA256).Hash
        $null=Invoke-Tool 'drawing.save_copy' @{path=$dwg} -FailureExpected
        Assert-Check ((Get-FileHash $dwg -Algorithm SHA256).Hash -eq $hash) 'Existing DWG overwrite rejected'
        $script:report.dwg=$dwg
        $script:report.status='passed-native-smoke-reopen-pending'
        Write-Host "Open $dwg in AutoCAD to verify hatch fill and dimension appearance. Reopen is not automatically verified."
    } else {$script:report.status='passed-read-only';Write-Host 'Read-only checks passed. Use -DrawTest with a new empty drawing for geometry and save checks.'}
} catch {
    if($script:report.status -eq 'running'){$script:report.status='failed'}
    $script:report.error=$_.Exception.Message
    Write-Host $_.Exception.Message
    $exitCode=1
} finally {
    $reportPath=Join-Path $OutputDirectory 'result.json'
    [IO.File]::WriteAllText($reportPath,($script:report | ConvertTo-Json -Depth 32),[Text.UTF8Encoding]::new($false))
    Write-Host "Report: $reportPath"
}
exit $exitCode
