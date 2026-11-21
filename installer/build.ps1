# Publishes Mochi and builds dist\Mochi-Setup-<version>.exe. Needs the .NET 9 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root 'dist\app'
Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\Mochi\Mochi.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $app
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Remove-Item "$app\*.pdb" -Force -ErrorAction SilentlyContinue

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found' }
& $iscc "$PSScriptRoot\mochi.iss"
