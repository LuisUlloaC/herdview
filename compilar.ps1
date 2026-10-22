# Compila Herdview y deja el ejecutable portable en dist\: primero los tests (hook y app); si fallan, no compila.
#   powershell -File compilar.ps1 [-Version 1.2.3]
param([string]$Version = '')
$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet).Source }

& python -I -m unittest discover -s (Join-Path $raiz 'hooks')
if ($LASTEXITCODE -ne 0) { throw 'Fallan los tests del hook' }
& $dotnet test --solution (Join-Path $raiz 'Herdview.slnx')
if ($LASTEXITCODE -ne 0) { throw 'Fallan los tests de la app' }
$extra = @()
if ($Version) { $extra += "-p:Version=$Version" }
& $dotnet build (Join-Path $raiz 'src\Herdview') -c Release @extra
if ($LASTEXITCODE -ne 0) { throw 'No compila' }

$dist = Join-Path $raiz 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $raiz 'src\Herdview\bin\Release\net472\Herdview.exe') $dist -Force
Write-Host "Listo: $dist\Herdview.exe"
