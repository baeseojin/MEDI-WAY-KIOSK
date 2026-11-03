# PowerShell 7; temporary DB, isolated cache prefix, test API port 5084.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$database='mediway_stafftest_'+[Guid]::NewGuid().ToString('N')
$process=$null
$oldPg=$env:ConnectionStrings__Postgres;$oldRedis=$env:ConnectionStrings__Redis;$oldPrefix=$env:QueueCache__KeyPrefix
Push-Location $root
function Assert($value,[string]$message){if(-not $value){throw "FAIL: $message"};Write-Output "PASS: $message"}
function Sql([string]$sql){$result=$sql|docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U mediway -d $database -At;if($LASTEXITCODE -ne 0){throw 'SQL failed'};return $result}
function Get([string]$path){return Invoke-RestMethod "http://127.0.0.1:5084/$path"}
function Register([int]$n,[string]$room){return Invoke-RestMethod 'http://127.0.0.1:5084/api/visits' -Method Post -ContentType application/json -Body (@{patientId=('30000000-0000-0000-0000-'+$n.ToString('D12'));roomId=$room;requestId=[Guid]::NewGuid()}|ConvertTo-Json)}
function Change([string]$id,[string]$action,[int]$version){$r=Invoke-WebRequest "http://127.0.0.1:5084/api/visits/$id/$action" -Method Post -ContentType application/json -Body (@{expectedVersion=$version}|ConvertTo-Json) -SkipHttpErrorCheck;return @{Status=[int]$r.StatusCode;Body=($r.Content|ConvertFrom-Json)}}
function KeyExists([string]$key){return docker compose exec -T redis redis-cli --raw exists $key}
try{
 docker compose exec -T postgres createdb -U mediway $database
 if($LASTEXITCODE -ne 0){throw 'Create DB failed'}
 foreach($file in Get-ChildItem database/init/*.sql|Sort-Object Name){$null=Sql (Get-Content $file.FullName -Raw)}
 $line=Get-Content .env|Where-Object{$_ -match '^POSTGRES_PASSWORD='}|Select-Object -First 1
 $cs=[System.Data.Common.DbConnectionStringBuilder]::new();$cs['Host']='127.0.0.1';$cs['Port']=55432;$cs['Database']=$database;$cs['Username']='mediway';$cs['Password']=$line.Substring('POSTGRES_PASSWORD='.Length).Trim();$cs['Timeout']=3
 $env:ConnectionStrings__Postgres=$cs.ConnectionString;$env:ConnectionStrings__Redis='127.0.0.1:56379';$env:QueueCache__KeyPrefix=$database
 $dll=Join-Path $root 'src/MediWay.Api/bin/Debug/net10.0/MediWay.Api.dll'
 $process=Start-Process dotnet -ArgumentList @(('"'+$dll+'"'),'--urls','http://127.0.0.1:5084') -PassThru -WindowStyle Hidden
 $started=$false;for($i=0;$i -lt 40;$i++){try{$null=Get 'health/live';$started=$true;break}catch{Start-Sleep -Milliseconds 250}}
 Assert $started 'Test API startup'
 $r1='20000000-0000-0000-0000-000000000001';$r2='20000000-0000-0000-0000-000000000002'
 $empty=Get "api/rooms/$r1/current";Assert ($null -eq $empty.visit) 'Empty room current response'
 $a=Register 1 $r1;$b=Register 2 $r1;$c=Register 3 $r2
 $list=Get "api/rooms/$r1/queue?refresh=true";$key="${database}:queue:$($list.serviceDate):$r1"
 Assert ((KeyExists $key) -eq '1') 'Queue cache populated before call'
 $ids=@($a.visit.id,$b.visit.id)
 $results=$ids|ForEach-Object -Parallel{
  $resp=Invoke-WebRequest "http://127.0.0.1:5084/api/visits/$_/call" -Method Post -ContentType application/json -Body '{"expectedVersion":1}' -SkipHttpErrorCheck
  [pscustomobject]@{Status=[int]$resp.StatusCode;Body=($resp.Content|ConvertFrom-Json)}
 } -ThrottleLimit 2
 Assert (@($results|Where-Object Status -eq 200).Count -eq 1 -and @($results|Where-Object Status -eq 409).Count -eq 1) 'Concurrent different-patient calls produce one winner'
 $winner=($results|Where-Object Status -eq 200).Body
 $loser=($results|Where-Object Status -eq 409).Body.current
 Assert (($results|Where-Object Status -eq 409).Body.code -eq 'ROOM_BUSY') 'Losing call reports ROOM_BUSY'
 Assert ((KeyExists $key) -eq '0') 'Call invalidates queue cache after commit'
 $current=Get "api/rooms/$r1/current";Assert ($current.visit.id -eq $winner.id -and $current.visit.status -eq 'Called') 'Current room matches winning call'
 $queue=Get "api/rooms/$r1/queue?refresh=true";Assert ($queue.items.Count -eq 1 -and $queue.items[0].visitId -eq $loser.id) 'Called visit leaves waiting queue'
 $callTime=Sql "SELECT called_at FROM visits WHERE id='$($winner.id)';"
 $replay=Change $winner.id call 1
 Assert ($replay.Status -eq 200 -and $replay.Body.version -eq 2 -and (Sql "SELECT called_at FROM visits WHERE id='$($winner.id)';") -eq $callTime) 'Call retry does not increment version or timestamp'
 $bad=Change $winner.id complete 2;Assert ($bad.Status -eq 409 -and $bad.Body.code -eq 'INVALID_TRANSITION') 'Cannot complete before starting'
 $stale=Change $winner.id start 1;Assert ($stale.Status -eq 409 -and $stale.Body.code -eq 'VERSION_CONFLICT') 'Stale version rejected with current state'
 $otherId=$c.visit.id
 $sameVisitResults=1..2|ForEach-Object -Parallel{
  $response=Invoke-WebRequest "http://127.0.0.1:5084/api/visits/$using:otherId/call" -Method Post -ContentType application/json -Body '{"expectedVersion":1}' -SkipHttpErrorCheck
  [pscustomobject]@{Status=[int]$response.StatusCode;Body=($response.Content|ConvertFrom-Json)}
 } -ThrottleLimit 2
 Assert (@($sameVisitResults|Where-Object {$_.Status -eq 200 -and $_.Body.version -eq 2}).Count -eq 2) 'Concurrent same-visit retries change state once in independent room'
 $null=Get "api/rooms/$r1/queue?refresh=true"
 $start=Change $winner.id start 2;Assert ($start.Status -eq 200 -and $start.Body.status -eq 'InService' -and $start.Body.version -eq 3) 'Start transitions Called to InService'
 Assert ((KeyExists $key) -eq '0') 'Start invalidates queue cache'
 $startReplay=Change $winner.id start 2;Assert ($startReplay.Status -eq 200 -and $startReplay.Body.version -eq 3) 'Start retry is idempotent'
 $cancelInService=Change $winner.id cancel 3;Assert ($cancelInService.Status -eq 409) 'InService visit cannot be cancelled'
 $null=Get "api/rooms/$r1/queue?refresh=true"
 $complete=Change $winner.id complete 3;Assert ($complete.Status -eq 200 -and $complete.Body.status -eq 'Completed' -and $complete.Body.version -eq 4) 'Complete transitions InService to Completed'
 Assert ((KeyExists $key) -eq '0') 'Complete invalidates queue cache'
 $repeatComplete=Change $winner.id complete 3;Assert ($repeatComplete.Status -eq 200 -and $repeatComplete.Body.version -eq 4) 'Completion retry is idempotent'
 Assert ($null -eq (Get "api/rooms/$r1/current").visit) 'Completion releases current room'
 Assert ((Sql "SELECT called_at IS NOT NULL AND started_at IS NOT NULL AND completed_at IS NOT NULL AND called_at<=started_at AND started_at<=completed_at FROM visits WHERE id='$($winner.id)';") -eq 't') 'Transition timestamps are recorded in order'
 $oldReplay=Change $winner.id call 1;Assert ($oldReplay.Status -eq 409) 'Old call cannot revert an advanced visit'
 $cancelCalled=Change $c.visit.id cancel 2;Assert ($cancelCalled.Status -eq 200 -and $cancelCalled.Body.status -eq 'Cancelled') 'Called visit can be cancelled'
 Assert ($null -eq (Get "api/rooms/$r2/current").visit) 'Cancellation releases called room'
 $cancelReplay=Change $c.visit.id cancel 2;Assert ($cancelReplay.Status -eq 200 -and $cancelReplay.Body.version -eq 3) 'Cancel retry is idempotent'
 $null=Get "api/rooms/$r1/queue?refresh=true"
 $cancelWaiting=Change $loser.id cancel 1;Assert ($cancelWaiting.Status -eq 200 -and $cancelWaiting.Body.status -eq 'Cancelled') 'Waiting visit can be cancelled'
 Assert ((KeyExists $key) -eq '0') 'Cancel invalidates queue cache'
 $invalid=Change $winner.id start 0;Assert ($invalid.Status -eq 400) 'Nonpositive version rejected'
 $missing=Change ([Guid]::NewGuid()) call 1;Assert ($missing.Status -eq 404) 'Unknown visit rejected'
 $roomMissing=Invoke-WebRequest "http://127.0.0.1:5084/api/rooms/$([Guid]::NewGuid())/current" -SkipHttpErrorCheck;Assert ($roomMissing.StatusCode -eq 404) 'Unknown room current returns 404'
 $past=Register 5 $r1;$null=Sql "UPDATE visits SET service_date=service_date-1 WHERE id='$($past.visit.id)';"
 $pastCall=Change $past.visit.id call 1;Assert ($pastCall.Status -eq 409 -and $pastCall.Body.code -eq 'PAST_VISIT') 'Previous-day waiting visit cannot be called'
 # Already-active previous-day visits must remain visible and finishable.
 $null=Sql "UPDATE visits SET status='Called',called_at=now(),version=2 WHERE id='$($past.visit.id)';"
 Assert ((Get "api/rooms/$r1/current").visit.id -eq $past.visit.id) 'Previous-day active visit remains visible'
 $pastStart=Change $past.visit.id start 2;$pastComplete=Change $past.visit.id complete 3
 Assert ($pastStart.Status -eq 200 -and $pastComplete.Status -eq 200) 'Previous-day active visit can finish normally'
 Write-Output 'ALL STEP 6 API TESTS PASSED'
}finally{
 if($process -and -not $process.HasExited){Stop-Process -Id $process.Id;$process.WaitForExit()}
 docker compose exec -T postgres dropdb -U mediway --if-exists --force $database
 $env:ConnectionStrings__Postgres=$oldPg;$env:ConnectionStrings__Redis=$oldRedis;$env:QueueCache__KeyPrefix=$oldPrefix
 Pop-Location
}
