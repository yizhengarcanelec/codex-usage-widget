$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $repoRoot 'src\GPTUsageWidget.cs'
$icon = Join-Path $repoRoot 'assets\GPTUsageWidget.ico'
$manifest = Join-Path $repoRoot 'src\app.manifest'
$release = Join-Path $repoRoot 'release'
$output = Join-Path $release 'GPTUsageWidget.exe'

$compilerCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'The .NET Framework C# compiler was not found.' }

New-Item -ItemType Directory -Force -Path $release | Out-Null
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu "/win32icon:$icon" "/win32manifest:$manifest" "/out:$output" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Core.dll $source
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

Write-Host "Built: $output"
