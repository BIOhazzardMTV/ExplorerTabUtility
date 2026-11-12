[CmdletBinding()]
param(
    [ValidateSet('net10.0-windows', 'net481')]
    [string]$Framework = 'net10.0-windows'
)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer was not found. Install Visual Studio or Build Tools with the .NET desktop development workload.'
}

$msbuild = & $vswhere -latest -products '*' -version '[18.0,)' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' |
    Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild 18.0 or newer was not found. Install Visual Studio 2026 or its Build Tools.'
}

$project = Join-Path $PSScriptRoot 'ExplorerTabUtility\ExplorerTabUtility.csproj'
& $msbuild $project /restore /m /t:Build /p:Configuration=Release "/p:TargetFramework=$Framework" /v:minimal
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exe = Join-Path $PSScriptRoot "ExplorerTabUtility\bin\Release\$Framework\ExplorerTabUtility.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "The build did not produce $exe"
}

Write-Output $exe
