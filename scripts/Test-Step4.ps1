# PowerShell 7. Uses an isolated DB and Redis container; never stops development Redis.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$suffix=[Guid]::NewGuid().ToString('N')
$database='mediway_cachetest_'+$suffix
$container='mediway-cachetest-'+$suffix
$prefix='test-'+$suffix
$process=$null
$oldPg=$env:ConnectionStrings__Postgres
$oldRedis=$env:ConnectionStrings__Redis
$oldPrefix=$env:QueueCache__KeyPrefix
Push-Location $root
function Assert($condition,[string]$message) {
    if (-not $condition) { throw "FAIL: $message" }
    Write-Output "PASS: $message"
}
function Sql([string]$query) {
    $output=$query | docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U mediway -d $database -At
    if ($LASTEXITCODE -ne 0) { throw 'SQL failed' }
    return $output
}
function RedisCommand([string[]]$arguments) {
    $output=docker exec $container redis-cli --raw @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Redis command failed' }
    return $output
}
function Queue([string]$room,[bool]$refresh=$false) {
    $url="http://127.0.0.1:5082/api/rooms/$room/queue"
    if ($refresh) { $url+='?refresh=true' }
    $r=Invoke-WebRequest $url -SkipHttpErrorCheck
    return @{Status=[int]$r.StatusCode;Source=($r.Headers['X-Queue-Source'] -join '');Body=($r.Content | ConvertFrom-Json)}
}
function Register([int]$patient,[string]$room) {
    $payload=@{patientId=('30000000-0000-0000-0000-'+$patient.ToString('D12'));roomId=$room;requestId=[Guid]::NewGuid()} | ConvertTo-Json
    $r=Invoke-WebRequest 'http://127.0.0.1:5082/api/visits' -Method Post -ContentType application/json -Body $payload -SkipHttpErrorCheck
    return @{Status=[int]$r.StatusCode;Body=($r.Content | ConvertFrom-Json)}
}
try {
    docker compose exec -T postgres createdb -U mediway $database
    if ($LASTEXITCODE -ne 0) { throw 'Test database creation failed' }
    foreach ($file in Get-ChildItem database/init/*.sql | Sort-Object Name) { $null=Sql (Get-Content $file.FullName -Raw) }
    # Select an available host port, then publish it explicitly so restart keeps the endpoint.
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
    $null=docker run -d --name $container -p "127.0.0.1:${port}:6379" redis:7.4
    if ($LASTEXITCODE -ne 0) { throw 'Test Redis startup failed' }
    $mapping=docker port $container 6379/tcp
    Assert ((($mapping -split ':')[-1]) -eq "$port") 'Isolated Redis host port is stable'
    $line=Get-Content .env | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
    $cs=[System.Data.Common.DbConnectionStringBuilder]::new()
    $cs['Host']='127.0.0.1';$cs['Port']=55432;$cs['Database']=$database
    $cs['Username']='mediway';$cs['Password']=$line.Substring('POSTGRES_PASSWORD='.Length).Trim();$cs['Timeout']=3
    $env:ConnectionStrings__Postgres=$cs.ConnectionString
    $env:ConnectionStrings__Redis="127.0.0.1:$port"
    $env:QueueCache__KeyPrefix=$prefix
    $dll=Join-Path $root 'src/MediWay.Api/bin/Debug/net10.0/MediWay.Api.dll'
    if (-not (Test-Path $dll)) { throw 'Run dotnet build first' }
    $process=Start-Process dotnet -ArgumentList @(('"'+$dll+'"'),'--urls','http://127.0.0.1:5082') -PassThru -WindowStyle Hidden
    $started=$false
    for ($i=0;$i -lt 40;$i++) {
        try { $null=Invoke-RestMethod 'http://127.0.0.1:5082/health/live';$started=$true;break } catch { Start-Sleep -Milliseconds 250 }
    }
    Assert $started 'Test API startup'
    $room1='20000000-0000-0000-0000-000000000001'
    $room2='20000000-0000-0000-0000-000000000002'
    $cold=Queue $room1
    Assert ($cold.Status -eq 200 -and $cold.Source -eq 'PostgreSQL' -and $cold.Body.items.Count -eq 0) 'Cold empty queue falls back to DB'
    $date=$cold.Body.serviceDate
    $key="${prefix}:queue:${date}:${room1}"
    $ttl=[int](RedisCommand @('ttl',$key))
    Assert ($ttl -gt 0 -and $ttl -le 5) 'Redis key has bounded 5-second TTL'
    $hit=Queue $room1
    Assert ($hit.Source -eq 'Redis' -and $hit.Body.items.Count -eq 0) 'Empty queue is cached and served from Redis'
    $first=Register 1 $room1
    Assert ($first.Status -eq 201) 'Registration succeeds with cached queue'
    Assert ((RedisCommand @('exists',$key)) -eq '0') 'Committed registration invalidates queue key'
    $loaded=Queue $room1
    Assert ($loaded.Source -eq 'PostgreSQL' -and $loaded.Body.items.Count -eq 1 -and $loaded.Body.items[0].queueNumber -eq 1) 'Cache rebuild includes new registration'
    $stored=RedisCommand @('get',$key)
    Assert ($stored -notmatch 'PatientId|DisplayName|PatientNumber') 'Cached queue omits patient identity fields'
    $second=Register 2 $room1
    $third=Register 3 $room2
    Assert ($second.Status -eq 201 -and $third.Status -eq 201) 'Multiple rooms accept registrations'
    $list=Queue $room1
    Assert ((($list.Body.items | ForEach-Object queueNumber) -join ',') -eq '1,2') 'Queue is ordered by ticket number'
    $other=Queue $room2
    Assert ($other.Body.items.Count -eq 1 -and $other.Body.items[0].queueNumber -eq 3) 'Queues are isolated per room'
    $null=Sql "UPDATE visits SET status='Called',called_at=now(),version=version+1 WHERE id='$($second.Body.visit.id)';"
    $fresh=Queue $room1 $true
    Assert ($fresh.Source -eq 'PostgreSQL' -and $fresh.Body.items.Count -eq 1) 'refresh=true bypasses stale cache and excludes Called visits'
    $freshHit=Queue $room1
    Assert ($freshHit.Source -eq 'Redis' -and $freshHit.Body.items.Count -eq 1) 'Forced refresh replaces cached snapshot'
    $null=Sql "UPDATE visits SET service_date=service_date-1 WHERE id='$($first.Body.visit.id)';"
    $today=Queue $room1 $true
    Assert ($today.Body.items.Count -eq 0) 'Queue excludes previous-day visits'
    $missing=Queue ([Guid]::NewGuid().ToString())
    Assert ($missing.Status -eq 404 -and $missing.Body.code -eq 'ROOM_NOT_FOUND') 'Unknown room returns 404'
    $null=RedisCommand @('set',$key,'not-json','EX','5')
    $repaired=Queue $room1
    Assert ($repaired.Status -eq 200 -and $repaired.Source -eq 'PostgreSQL') 'Malformed cache recovers from DB'
    $null=RedisCommand @('del',$key)
    $rebuilt=Queue $room1
    Assert ($rebuilt.Source -eq 'PostgreSQL') 'Deleted cache rebuilds from DB'
    Start-Sleep -Milliseconds 5500
    $expired=Queue $room1
    Assert ($expired.Source -eq 'PostgreSQL') 'Expired cache reloads from DB'
    $null=docker stop --time 1 $container
    if ($LASTEXITCODE -ne 0) { throw 'Unable to stop isolated Redis' }
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $fallback=Queue $room1
    $timer.Stop()
    Assert ($fallback.Status -eq 200 -and $fallback.Source -eq 'PostgreSQL' -and $timer.Elapsed.TotalSeconds -lt 8) 'Redis outage falls back without indefinite wait'
    $offlineRegistration=Register 4 $room1
    Assert ($offlineRegistration.Status -eq 201) 'Redis invalidation failure does not fail committed registration'
    $offlineQueue=Queue $room1
    Assert ($offlineQueue.Body.items.Count -eq 1 -and $offlineQueue.Body.items[0].visitId -eq $offlineRegistration.Body.visit.id) 'DB fallback shows registration created during Redis outage'
    $null=docker start $container
    if ($LASTEXITCODE -ne 0) { throw 'Unable to restart isolated Redis' }
    $mapping=docker port $container 6379/tcp
    Assert ((($mapping -split ':')[-1]) -eq "$port") 'Redis restart preserves published endpoint'
    $recovered=$false
    for ($i=0;$i -lt 20;$i++) {
        $null=Queue $room1 $true
        $r=Queue $room1
        if ($r.Source -eq 'Redis' -and $r.Body.items.Count -eq 1) { $recovered=$true;break }
        Start-Sleep -Milliseconds 500
    }
    Assert $recovered 'Cache resumes after Redis restarts'
    Write-Output 'ALL STEP 4 TESTS PASSED'
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    $null=docker rm -f $container
    docker compose exec -T postgres dropdb -U mediway --if-exists --force $database
    $env:ConnectionStrings__Postgres=$oldPg;$env:ConnectionStrings__Redis=$oldRedis;$env:QueueCache__KeyPrefix=$oldPrefix
    Pop-Location
}
