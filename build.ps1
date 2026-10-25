<#
  Builds DeskMeters (a single, self-contained .NET Framework 4.8 exe).

  Needs the .NET SDK 8 or newer:   winget install Microsoft.DotNet.SDK.8
  The .NET Framework 4.8 reference assemblies are fetched from NuGet on the first build.

  Output:
    dist\DeskMeters.exe        portable app (just run it)
    dist\DeskMeters-Setup.exe  the same exe; when its file name contains "Setup" it runs the installer
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK isn't installed. Run: winget install Microsoft.DotNet.SDK.8"
}

dotnet build .\DeskMeters.csproj -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

New-Item -ItemType Directory -Force -Path .\dist | Out-Null
Copy-Item .\bin\Release\DeskMeters.exe .\dist\DeskMeters.exe -Force
Copy-Item .\bin\Release\DeskMeters.exe .\dist\DeskMeters-Setup.exe -Force

$exe = Get-Item .\dist\DeskMeters.exe
Write-Host ""
Write-Host ("Built {0} ({1:N1} MB) and DeskMeters-Setup.exe" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
