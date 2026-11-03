# PowerShell 7; existing development PostgreSQL and Redis must be running.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
 dotnet build
 if($LASTEXITCODE -ne 0){throw 'Solution build failed'}
 foreach($project in @('MediWay.Domain.Tests','MediWay.Kiosk.Tests','MediWay.Staff.Tests','MediWay.Display.Tests','MediWay.Navigation.Tests')) {
  Write-Output "CHECK: $project"
  dotnet run --project "tests/$project" --no-build
  if($LASTEXITCODE -ne 0){throw "$project failed"}
 }
 foreach($step in @(3,4,6,7,8,9,14,15)) {
  Write-Output "CHECK: Step $step integration"
  pwsh -NoProfile -File (Join-Path $PSScriptRoot "Test-Step$step.ps1")
  if($LASTEXITCODE -ne 0){throw "Step $step failed"}
 }
 pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-TwoFloors.ps1')
 if($LASTEXITCODE -ne 0){throw 'Two-floor integration failed'}
 Write-Output 'ALL MEDI-WAY AUTOMATED CHECKS PASSED'
} finally { Pop-Location }
