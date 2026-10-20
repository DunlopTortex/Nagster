$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'DGNaggingAlarms\DGNaggingAlarms.csproj'
$publish = Join-Path $PSScriptRoot 'publish'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$compiler = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $compiler)) { throw 'Inno Setup 6 is required to build the installer.' }
& $compiler (Join-Path $PSScriptRoot 'installer\DGNaggingAlarms.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
