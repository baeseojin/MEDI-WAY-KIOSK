# Windows PowerShell 5.1 or PowerShell 7; isolated resources. Does not stop development apps.
[CmdletBinding()]
param([ValidateRange(120,86400)][int]$DurationSeconds=1800,[ValidateRange(5,30)][int]$OutageSeconds=10,[ValidateRange(1024,65535)][int]$Port=5118)
$ErrorActionPreference='Stop'
$oldOutputEncoding=$OutputEncoding
$oldConsoleEncoding=[Console]::OutputEncoding
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$OutputEncoding=[Text.UTF8Encoding]::new($false)
$root=Split-Path $PSScriptRoot -Parent
$runId=[Guid]::NewGuid().ToString('N')
$database='mediway_stability_'+$runId
$report=Join-Path $root ('validation/step18/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$runId.Substring(0,8))
$build=Join-Path $root ('.runtime/stability-build-'+$runId)
$apiProcess=$null;$clientProcess=$null;$created=$false;$failure=$null
$oldPg=$env:ConnectionStrings__Postgres;$oldRedis=$env:ConnectionStrings__Redis;$oldPrefix=$env:QueueCache__KeyPrefix
New-Item -ItemType Directory -Force -Path $report|Out-Null
Push-Location $root
try {
 $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$Port);try{$listener.Start()}finally{$listener.Stop()}
 dotnet build MediWay.slnx --artifacts-path $build *> (Join-Path $report 'build.log')
 if($LASTEXITCODE -ne 0){throw 'Stability build failed; see build.log'}
 docker compose exec -T postgres createdb -U mediway $database
 if($LASTEXITCODE -ne 0){throw 'Temporary database creation failed'};$created=$true
 foreach($file in Get-ChildItem database/init/*.sql|Sort-Object Name){Get-Content $file.FullName -Raw -Encoding UTF8|docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U mediway -d $database|Out-Null;if($LASTEXITCODE -ne 0){throw 'Test schema failed'}}
 $line=Get-Content .env|Where-Object{$_ -match '^POSTGRES_PASSWORD='}|Select-Object -First 1
 if(-not $line){throw 'PostgreSQL password configuration missing'}
 $cs=[System.Data.Common.DbConnectionStringBuilder]::new();$cs['Host']='127.0.0.1';$cs['Port']=55432;$cs['Database']=$database;$cs['Username']='mediway';$cs['Password']=$line.Substring('POSTGRES_PASSWORD='.Length).Trim();$cs['Timeout']=3
 $env:ConnectionStrings__Postgres=$cs.ConnectionString;$env:ConnectionStrings__Redis='127.0.0.1:56379';$env:QueueCache__KeyPrefix=$database
 $address="http://127.0.0.1:$Port/";$apiDll=Join-Path $build 'bin/MediWay.Api/debug/MediWay.Api.dll'
 function Start-TestApi([int]$generation){
  $p=Start-Process dotnet -ArgumentList @(('"'+$apiDll+'"'),'--urls',$address) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $report "api-$generation.log") -RedirectStandardError (Join-Path $report "api-$generation-error.log")
  $healthy=$false
  for($i=0;$i -lt 60;$i++){if($p.HasExited){throw 'Test API exited during startup'};try{$h=Invoke-RestMethod ($address+'health/ready') -TimeoutSec 1;if($h.status -eq 'Healthy'){$healthy=$true;break}}catch{};Start-Sleep -Milliseconds 250}
  if(-not $healthy){Stop-Process -Id $p.Id;throw 'API readiness timeout'}
  return $p
 }
 $apiProcess=Start-TestApi 1
 $testDll=Join-Path $build 'bin/MediWay.Stability.Tests/debug/MediWay.Stability.Tests.dll'
 $clientProcess=Start-Process dotnet -ArgumentList @(('"'+$testDll+'"'),$address,$DurationSeconds,('"'+$report+'"')) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $report 'client.log') -RedirectStandardError (Join-Path $report 'client-error.log')
 # Retain a native process handle so Windows PowerShell 5.1 can read ExitCode after polling HasExited.
 $null=$clientProcess.Handle
 $watch=[Diagnostics.Stopwatch]::StartNew();$stopped=$false;$restarted=$false;$stoppedAt=0;$nextSample=0
 'elapsed_seconds,pid,private_bytes,working_set_bytes,handles,threads,pg_connections'|Set-Content (Join-Path $report 'server-metrics.csv') -Encoding utf8
 while(-not $clientProcess.HasExited){
  if($watch.Elapsed.TotalSeconds -gt $DurationSeconds+150){throw 'Stability client exceeded bounded runtime'}
  if(-not $stopped -and $watch.Elapsed.TotalSeconds -ge $DurationSeconds/2){
   Stop-Process -Id $apiProcess.Id;$apiProcess.WaitForExit();$stopped=$true;$stoppedAt=$watch.Elapsed.TotalSeconds
   Write-Output ('API_OUTAGE elapsed='+[Math]::Round($stoppedAt,1))
  }
  if($stopped -and -not $restarted -and $watch.Elapsed.TotalSeconds-$stoppedAt -ge $OutageSeconds){
   $apiProcess=Start-TestApi 2;$restarted=$true;Write-Output ('API_RESTART elapsed='+[Math]::Round($watch.Elapsed.TotalSeconds,1))
  }
  if($watch.Elapsed.TotalSeconds -ge $nextSample){
   if($apiProcess -and -not $apiProcess.HasExited){
    $apiProcess.Refresh()
    $connections=docker compose exec -T postgres psql -U mediway -d mediway -At -c "SELECT count(*) FROM pg_stat_activity WHERE datname='$database';"
    if($LASTEXITCODE -ne 0){throw 'DB connection sample failed'}
    $row=('{0:F2},{1},{2},{3},{4},{5},{6}' -f $watch.Elapsed.TotalSeconds,$apiProcess.Id,$apiProcess.PrivateMemorySize64,$apiProcess.WorkingSet64,$apiProcess.HandleCount,$apiProcess.Threads.Count,$connections)
    Add-Content (Join-Path $report 'server-metrics.csv') $row -Encoding utf8
   }
   Write-Output ('SAMPLE elapsed='+[Math]::Round($watch.Elapsed.TotalSeconds,1))
   $nextSample=$watch.Elapsed.TotalSeconds+5
  }
  Start-Sleep -Milliseconds 250
 }
 $clientProcess.WaitForExit()
 if($null -eq $clientProcess.ExitCode -or $clientProcess.ExitCode -ne 0){throw 'Stability assertions failed; see client.log and client-error.log'}
 if(-not $restarted){throw 'Outage injection did not complete'}
 $summary=Get-Content (Join-Path $report 'summary.json') -Raw|ConvertFrom-Json
 $roomNames=@(docker compose exec -T postgres psql -U mediway -d $database -At -c "SELECT name FROM rooms ORDER BY code;")
 if($LASTEXITCODE -ne 0 -or ($roomNames -join '|') -ne '내과 1진료실|내과 2진료실|외과 3진료실|외과 4진료실'){throw 'UTF-8 room seed verification failed'}
 Write-Output 'PASS: UTF-8 Korean room seed names remain intact'
 $dbResult=docker compose exec -T postgres psql -U mediway -d $database -At -F ',' -c "SELECT count(*),count(*) FILTER(WHERE status='Completed'),count(*) FILTER(WHERE status IN ('Waiting','Called','InService')),count(DISTINCT (department_id,service_date,queue_number)) FROM visits;"
 if($LASTEXITCODE -ne 0){throw 'Final database audit failed'}
 $expected="$($summary.Cycles),$($summary.Cycles),0,$($summary.Cycles)"
 if($dbResult -ne $expected){throw "Final database lifecycle/number audit mismatch: $dbResult"}
 if(@(Get-ChildItem (Join-Path $report 'Tickets') -Filter '*.txt').Count -ne $summary.Cycles){throw 'Ticket file count mismatch'}
 if(Test-Path (Join-Path $report 'Offline/pending-registration.json')){throw 'Pending request remains after completed run'}
 $dbResult|Set-Content (Join-Path $report 'database-audit.csv') -Encoding utf8
 Write-Output 'PASS: Every visit completed exactly once with unique department/day number and one ticket file'
 Write-Output 'PASS: No active visit or pending recovery request remains'
 Write-Output ('STABILITY_REPORT='+$report)
} catch {
 $failure=$_
 $_.Exception.Message|Set-Content (Join-Path $report 'failure.txt') -Encoding utf8
 throw
} finally {
 if($clientProcess -and -not $clientProcess.HasExited){Stop-Process -Id $clientProcess.Id;$clientProcess.WaitForExit()}
 if($apiProcess -and -not $apiProcess.HasExited){Stop-Process -Id $apiProcess.Id;$apiProcess.WaitForExit()}
 if($database -notmatch '^mediway_stability_[a-f0-9]{32}$'){throw 'Unsafe cleanup namespace'}
 $keys=@(docker compose exec -T redis redis-cli --scan --pattern "${database}:*")
 foreach($key in $keys){if(-not $key.StartsWith("${database}:")){throw 'Unexpected cache namespace'};$null=docker compose exec -T redis redis-cli DEL $key}
 if($created){docker compose exec -T postgres dropdb -U mediway --if-exists --force $database;if($LASTEXITCODE -ne 0){Write-Warning 'Temporary DB cleanup failed'}}
 $env:ConnectionStrings__Postgres=$oldPg;$env:ConnectionStrings__Redis=$oldRedis;$env:QueueCache__KeyPrefix=$oldPrefix
 [Console]::OutputEncoding=$oldConsoleEncoding
 $OutputEncoding=$oldOutputEncoding
 Pop-Location
}