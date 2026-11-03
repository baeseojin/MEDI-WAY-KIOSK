# Integration tests use a temporary database; the development database is untouched.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$database = 'mediway_test_' + [Guid]::NewGuid().ToString('N')
$process = $null
$oldPg = $env:ConnectionStrings__Postgres
$oldRedis = $env:ConnectionStrings__Redis
Push-Location $root
function Sql([string]$query) {
    $output = $query | docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U mediway -d $database -At
    if ($LASTEXITCODE -ne 0) { throw 'SQL failed' }
    return $output
}
function Assert($condition, [string]$message) {
    if (-not $condition) { throw "FAIL: $message" }
    Write-Output "PASS: $message"
}
function Post($body) {
    $r = Invoke-WebRequest 'http://127.0.0.1:5081/api/visits' -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json) -SkipHttpErrorCheck
    return @{ Status = [int]$r.StatusCode; Body = ($r.Content | ConvertFrom-Json) }
}
try {
    docker compose exec -T postgres createdb -U mediway $database
    if ($LASTEXITCODE -ne 0) { throw 'Test database creation failed' }
    foreach ($file in Get-ChildItem database/init/*.sql | Sort-Object Name) { $null = Sql (Get-Content $file.FullName -Raw) }
    $passwordLine = Get-Content .env | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
    $cs = [System.Data.Common.DbConnectionStringBuilder]::new()
    $cs['Host']='127.0.0.1'; $cs['Port']=55432; $cs['Database']=$database
    $cs['Username']='mediway'; $cs['Password']=$passwordLine.Substring('POSTGRES_PASSWORD='.Length).Trim(); $cs['Timeout']=3
    $env:ConnectionStrings__Postgres=$cs.ConnectionString
    $env:ConnectionStrings__Redis='127.0.0.1:56379'
    $dll = Join-Path $root 'src/MediWay.Api/bin/Debug/net10.0/MediWay.Api.dll'
    if (-not (Test-Path $dll)) { throw 'Run dotnet build first' }
    $process = Start-Process dotnet -ArgumentList @(('"' + $dll + '"'),'--urls','http://127.0.0.1:5081') -PassThru -WindowStyle Hidden
    $started=$false
    for ($i=0; $i -lt 40; $i++) {
        try { $null=Invoke-RestMethod 'http://127.0.0.1:5081/health/live'; $started=$true; break } catch { Start-Sleep -Milliseconds 250 }
    }
    Assert $started 'API startup'
    $patient=Invoke-RestMethod 'http://127.0.0.1:5081/api/patients/by-number/p0001'
    Assert ($patient.patientNumber -eq 'P0001') 'Patient lookup and normalization'
    $missing=Invoke-WebRequest 'http://127.0.0.1:5081/api/patients/by-number/unknown' -SkipHttpErrorCheck
    Assert ($missing.StatusCode -eq 404) 'Unknown patient number returns 404'
    $rooms=Invoke-RestMethod 'http://127.0.0.1:5081/api/rooms'
    Assert ($rooms.Count -eq 4) 'Room lookup'
    $body=@{patientId=$patient.id;roomId=$rooms[0].id;requestId=[Guid]::NewGuid().ToString()}
    $created=Post $body
    Assert ($created.Status -eq 201 -and $created.Body.visit.queueNumber -eq 1 -and $created.Body.visit.status -eq 'Waiting') 'First visit created with queue 1'
    $retry=Post $body
    Assert ($retry.Status -eq 200 -and $retry.Body.visit.id -eq $created.Body.visit.id) 'Same request returns original visit'
    $conflict=Post (@{patientId=$patient.id;roomId=$rooms[1].id;requestId=$body.requestId})
    Assert ($conflict.Status -eq 409 -and $conflict.Body.code -eq 'REQUEST_KEY_CONFLICT') 'Request key payload conflict'
    $secondKey=@{patientId=$patient.id;roomId=$rooms[1].id;requestId=[Guid]::NewGuid().ToString()}
    $duplicate=Post $secondKey
    Assert ($duplicate.Status -eq 200 -and $duplicate.Body.visit.id -eq $created.Body.visit.id -and $duplicate.Body.visit.roomId -eq $rooms[0].id) 'Active duplicate keeps original room and visit'
    $null=Sql "UPDATE visits SET status='Completed',completed_at=now(),version=version+1 WHERE id='$($created.Body.visit.id)';"
    $afterComplete=Post $secondKey
    Assert ($afterComplete.Status -eq 200 -and $afterComplete.Body.visit.id -eq $created.Body.visit.id) 'Duplicate request history survives completion'
    $newVisit=Post (@{patientId=$patient.id;roomId=$rooms[0].id;requestId=[Guid]::NewGuid().ToString()})
    Assert ($newVisit.Status -eq 201 -and $newVisit.Body.visit.queueNumber -eq 2) 'New request after completion gets next number'
    $empty=Post (@{patientId=[Guid]::Empty;roomId=$rooms[0].id;requestId=[Guid]::NewGuid()})
    Assert ($empty.Status -eq 400) 'Empty identifier rejected'
    $unknown=Post (@{patientId=[Guid]::NewGuid();roomId=$rooms[0].id;requestId=[Guid]::NewGuid()})
    Assert ($unknown.Status -eq 404) 'Unknown patient ID rejected'
    $unknownRoom=Post (@{patientId=$patient.id;roomId=[Guid]::NewGuid();requestId=[Guid]::NewGuid()})
    Assert ($unknownRoom.Status -eq 404) 'Unknown room ID rejected'
    $room=$rooms[0].id
    # Four patients, five concurrent retries each. Both queue and idempotency locks are exercised.
    $results=1..20 | ForEach-Object -Parallel {
        $n=2+(($_-1)%4)
        $payload=@{patientId=('30000000-0000-0000-0000-'+$n.ToString('D12'));roomId=$using:room;requestId=('40000000-0000-0000-0000-'+$n.ToString('D12'))} | ConvertTo-Json
        $r=Invoke-WebRequest 'http://127.0.0.1:5081/api/visits' -Method Post -ContentType 'application/json' -Body $payload -SkipHttpErrorCheck
        [pscustomobject]@{Status=[int]$r.StatusCode;Data=($r.Content | ConvertFrom-Json)}
    } -ThrottleLimit 20
    Assert (@($results | Where-Object Status -eq 201).Count -eq 4 -and @($results | Where-Object Status -eq 200).Count -eq 16) '20 concurrent requests create exactly four visits'
    $numbers=@($results | ForEach-Object { $_.Data.visit.queueNumber } | Sort-Object -Unique)
    Assert (($numbers -join ',') -eq '3,4,5,6') 'Concurrent queue numbers are unique and sequential'
    Assert ((Sql 'SELECT count(*) FROM visits;') -eq '6') 'Only expected rows persisted'
    $read=Invoke-RestMethod "http://127.0.0.1:5081/api/visits/$($created.Body.visit.id)"
    Assert ($read.status -eq 'Completed') 'Visit read returns current persisted state'
    $date=Sql "SELECT (clock_timestamp() AT TIME ZONE 'Asia/Seoul')::date;"
    Assert ($newVisit.Body.visit.serviceDate -eq $date) 'Service date follows Asia/Seoul database time'
    $null=Sql "UPDATE visits SET status='Cancelled',cancelled_at=now(),version=version+1 WHERE id='$($newVisit.Body.visit.id)';"
    $afterCancel=Post (@{patientId=$patient.id;roomId=$rooms[0].id;requestId=[Guid]::NewGuid().ToString()})
    Assert ($afterCancel.Status -eq 201 -and $afterCancel.Body.visit.queueNumber -eq 7) 'Cancelled queue number is not reused'
    $malformed=Invoke-WebRequest 'http://127.0.0.1:5081/api/visits' -Method Post -ContentType 'application/json' -Body '{"patientId":"invalid"}' -SkipHttpErrorCheck
    Assert ($malformed.StatusCode -eq 400) 'Malformed identifier returns 400'
    Stop-Process -Id $process.Id; $process.WaitForExit()
    $cs['Database']=$database+'_missing'
    $env:ConnectionStrings__Postgres=$cs.ConnectionString
    $process=Start-Process dotnet -ArgumentList @(('"'+$dll+'"'),'--urls','http://127.0.0.1:5081') -PassThru -WindowStyle Hidden
    $started=$false
    for ($i=0; $i -lt 40; $i++) {
        try { $null=Invoke-RestMethod 'http://127.0.0.1:5081/health/live'; $started=$true; break } catch { Start-Sleep -Milliseconds 250 }
    }
    Assert $started 'API startup with unavailable database'
    $failed=Post $body
    Assert ($failed.Status -eq 503 -and $failed.Body.code -eq 'DATABASE_UNAVAILABLE') 'Database failure returns 503 instead of successful registration'
    Write-Output 'ALL STEP 3 TESTS PASSED'
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    docker compose exec -T postgres dropdb -U mediway --if-exists --force $database
    $env:ConnectionStrings__Postgres=$oldPg; $env:ConnectionStrings__Redis=$oldRedis
    Pop-Location
}
