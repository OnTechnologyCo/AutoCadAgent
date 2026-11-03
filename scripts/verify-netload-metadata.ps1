param([string]$AutoCADSdkDir=$env:AUTOCAD_SDK_DIR)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $AutoCADSdkDir){$AutoCADSdkDir=Join-Path $root '.sdk\AutoCAD2024'}
$dll=Join-Path $root 'artifacts\app\cad\AutoCadAgent.AutoCAD.dll'
# Metadata-only inspection: do not execute the Autodesk native code outside AutoCAD.
$resolver=[ResolveEventHandler]{param($sender,$event)
    $name=[Reflection.AssemblyName]::new($event.Name)
    $candidate=Join-Path $AutoCADSdkDir ($name.Name+'.dll')
    if(Test-Path $candidate){return [Reflection.Assembly]::ReflectionOnlyLoadFrom($candidate)}
    return [Reflection.Assembly]::ReflectionOnlyLoad($event.Name)
}
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($resolver)
try {
    $assembly=[Reflection.Assembly]::ReflectionOnlyLoadFrom($dll)
    $attributes=@($assembly.GetCustomAttributesData())
    $framework=($attributes | Where-Object {$_.AttributeType.FullName -eq 'System.Runtime.Versioning.TargetFrameworkAttribute'}).ConstructorArguments[0].Value
    if($framework -ne '.NETFramework,Version=v4.8'){throw "Incorrect NETLOAD target: $framework"}
    if($assembly.GetName().ProcessorArchitecture -ne 'Amd64'){throw 'Expected x64 plugin'}
    foreach($kind in @('ExtensionApplicationAttribute','CommandClassAttribute')){
        $attribute=@($attributes | Where-Object {$_.AttributeType.FullName -eq ('Autodesk.AutoCAD.Runtime.'+$kind)})
        if($attribute.Count -ne 1 -or $attribute[0].ConstructorArguments[0].Value.FullName -ne 'AutoCadAgent.Cad.BridgePlugin'){throw "Incorrect $kind registration"}
    }
    $plugin=$assembly.GetType('AutoCadAgent.Cad.BridgePlugin')
    if(@($plugin.GetInterfaces() | Where-Object FullName -eq 'Autodesk.AutoCAD.Runtime.IExtensionApplication').Count -ne 1){throw 'Missing IExtensionApplication interface'}
    $commands=@($plugin.GetMethods() | ForEach-Object {foreach($attribute in $_.GetCustomAttributesData()){if($attribute.AttributeType.FullName -eq 'Autodesk.AutoCAD.Runtime.CommandMethodAttribute'){$attribute.ConstructorArguments[0].Value}}})
    foreach($required in @('CADAGENTSTART','CADAGENTSTOP','CADAGENTSTATUS')){if($commands -notcontains $required){throw "Missing command: $required"}}
    $references=@($assembly.GetReferencedAssemblies() | ForEach-Object { @{name=$_.Name;version=$_.Version.ToString()} })
    $allowed=@('mscorlib','System','System.Core','System.Web.Extensions','System.Windows.Forms','AcMgd','AcDbMgd','AcCoreMgd')
    foreach($reference in $references){if($allowed -notcontains $reference.name){throw ('Unexpected dependency: '+$reference.name)}}
    $toolTypes=@($assembly.GetTypes() | Where-Object {$_.BaseType.FullName -eq 'AutoCadAgent.Cad.ChatTool'}).Count
    if($toolTypes -ne 122){throw "Expected 122 tool types; got $toolTypes"}
    $dir=Split-Path $dll
    foreach($sdkDll in @('AcMgd.dll','AcDbMgd.dll','AcCoreMgd.dll')){if(Test-Path (Join-Path $dir $sdkDll)){throw "SDK DLL must not be distributed: $sdkDll"}}
    [ordered]@{status='passed-metadata-only';framework=$framework;architecture='x64';commands=$commands;toolTypes=$toolTypes;references=$references;sha256=(Get-FileHash $dll -Algorithm SHA256).Hash;nativeNetload='not tested by metadata inspection'} | ConvertTo-Json -Depth 5
} finally {[AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolver)}
