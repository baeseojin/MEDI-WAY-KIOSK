# PowerShell 7; isolated DB and API port 5088. Closures affect this test DB only.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$database='mediway_navigationtest_'+[Guid]::NewGuid().ToString('N');$process=$null
$oldPg=$env:ConnectionStrings__Postgres;$oldRedis=$env:ConnectionStrings__Redis;$oldPrefix=$env:QueueCache__KeyPrefix
Push-Location $root
function Assert($value,[string]$label){if(-not $value){throw "FAIL: $label"};Write-Output "PASS: $label"}
function Sql([string]$query){$value=$query|docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U mediway -d $database -At;if($LASTEXITCODE -ne 0){throw 'SQL failed'};return $value}
function Get([string]$path){$r=Invoke-WebRequest "http://127.0.0.1:5088/$path" -SkipHttpErrorCheck;return @{Status=[int]$r.StatusCode;Body=($r.Content|ConvertFrom-Json)}}
try{
 $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,5088);try{$listener.Start()}finally{$listener.Stop()}
 docker compose exec -T postgres createdb -U mediway $database;if($LASTEXITCODE -ne 0){throw 'Create failed'}
 foreach($file in Get-ChildItem database/init/*.sql|Sort-Object Name){$null=Sql (Get-Content $file.FullName -Raw)}
 $line=Get-Content .env|Where-Object{$_ -match '^POSTGRES_PASSWORD='}|Select-Object -First 1
 $cs=[System.Data.Common.DbConnectionStringBuilder]::new();$cs['Host']='127.0.0.1';$cs['Port']=55432;$cs['Database']=$database;$cs['Username']='mediway';$cs['Password']=$line.Substring('POSTGRES_PASSWORD='.Length).Trim();$cs['Timeout']=3
 $env:ConnectionStrings__Postgres=$cs.ConnectionString;$env:ConnectionStrings__Redis='127.0.0.1:56379';$env:QueueCache__KeyPrefix=$database
 $dll=Join-Path $root 'src/MediWay.Api/bin/Debug/net10.0/MediWay.Api.dll'
 $process=Start-Process dotnet -ArgumentList @(('"'+$dll+'"'),'--urls','http://127.0.0.1:5088') -WindowStyle Hidden -PassThru
 $ready=$false;for($i=0;$i -lt 40;$i++){try{$null=Invoke-RestMethod http://127.0.0.1:5088/health/live;$ready=$true;break}catch{Start-Sleep -Milliseconds 250}}
 Assert $ready 'Test API startup'
 $map=Get 'api/maps/F1';Assert ($map.Status -eq 200 -and $map.Body.nodes.Count -eq 12 -and $map.Body.links.Count -eq 13 -and $map.Body.zones.Count -eq 6) 'Seeded floor geometry loads from DB'
 $start='61000000-0000-0000-0000-000000000002';$r1='20000000-0000-0000-0000-000000000001';$r2='20000000-0000-0000-0000-000000000002'
 $path="api/navigation/rooms/$r1/route?startNodeId=$start"
 $first=Get $path
 Assert ($first.Status -eq 200 -and $first.Body.algorithm -eq 'AStar' -and $first.Body.totalCost -eq 58 -and $first.Body.distanceMeters -eq 58) 'Room route uses A* with expected baseline cost and distance'
 Assert ($first.Body.points[0].id -eq $start -and $first.Body.destinationNodeId -eq '61000000-0000-0000-0000-000000000009') 'Route begins at kiosk and ends at room entrance'
 $r3='20000000-0000-0000-0000-000000000003';$r4='20000000-0000-0000-0000-000000000004'
 $floor2=Get 'api/maps/F2';Assert ($floor2.Status -eq 200 -and $floor2.Body.nodes.Count -eq 6) 'Second-floor surgery map loads'
 $surgery=Get "api/navigation/rooms/$r3/route?startNodeId=$start"
 Assert ($surgery.Status -eq 200 -and $surgery.Body.legs.Count -eq 2 -and $surgery.Body.legs[0].map.code -eq 'F1' -and $surgery.Body.legs[1].map.code -eq 'F2') 'Cross-floor A* returns separate elevator and surgery map legs'
 Assert ($surgery.Body.totalCost -eq 114 -and $surgery.Body.distanceMeters -eq 103) 'Cross-floor totals include transfer cost and distance'
 $fourth=Get "api/navigation/rooms/$r4/route?startNodeId=$start"
 Assert ($fourth.Status -eq 200 -and $fourth.Body.totalCost -eq 134 -and $fourth.Body.destinationNodeId -eq '64000000-0000-0000-0000-000000000006') 'Room 4 resolves its own second-floor entrance'
 $reverse=Get "api/navigation/routes?startNodeId=64000000-0000-0000-0000-000000000004&destinationNodeId=$start"
 Assert ($reverse.Status -eq 200 -and $reverse.Body.legs[0].map.code -eq 'F2' -and $reverse.Body.legs[1].map.code -eq 'F1') 'Bidirectional elevator supports reverse floor navigation'
 $null=Sql "UPDATE map_floor_connections SET is_closed=true;"
 $closed=Get "api/navigation/rooms/$r3/route?startNodeId=$start"
 Assert ($closed.Status -eq 409 -and $closed.Body.code -eq 'ROUTE_NOT_FOUND') 'Closed elevator blocks cross-floor route'
 Assert ((Get $path).Status -eq 200) 'Closed elevator leaves first-floor navigation available'
 $null=Sql "UPDATE map_floor_connections SET is_closed=false;"
 $second=Get "api/navigation/rooms/$r2/route?startNodeId=$start"
 Assert ($second.Status -eq 200 -and $second.Body.destinationNodeId -eq '61000000-0000-0000-0000-000000000010' -and $second.Body.totalCost -eq 78) 'Second room resolves its own mapped destination'
 $null=Sql "UPDATE map_links SET is_closed=true WHERE id='62000000-0000-0000-0000-000000000012';"
 $detour=Get $path
 Assert ($detour.Status -eq 200 -and $detour.Body.totalCost -eq 64 -and $detour.Body.points.id -notcontains '61000000-0000-0000-0000-000000000012') 'Closing central passage immediately selects alternate corridor'
 Assert (($detour.Body.map.links|Where-Object id -eq '62000000-0000-0000-0000-000000000012').isClosed) 'Response map and route share closed-link snapshot'
 $null=Sql "UPDATE map_links SET weight=100 WHERE id='62000000-0000-0000-0000-000000000005';"
 $weighted=Get $path
 Assert ($weighted.Status -eq 200 -and $weighted.Body.totalCost -eq 98 -and $weighted.Body.points.id -contains '61000000-0000-0000-0000-000000000005') 'Weight change selects lower-cost eastern detour'
 $null=Sql "UPDATE map_links SET is_closed=true WHERE id='62000000-0000-0000-0000-000000000009';"
 $blocked=Get $path;Assert ($blocked.Status -eq 409 -and $blocked.Body.code -eq 'ROUTE_NOT_FOUND') 'Isolated room reports no available route'
 $null=Sql "UPDATE map_links SET is_closed=false;UPDATE map_links SET is_bidirectional=false WHERE id='62000000-0000-0000-0000-000000000009';"
 $reverse=Get "api/navigation/routes?startNodeId=61000000-0000-0000-0000-000000000009&destinationNodeId=$start"
 Assert ($reverse.Status -eq 409 -and $reverse.Body.code -eq 'ROUTE_NOT_FOUND') 'API respects one-way passage orientation'
 $same=Get "api/navigation/routes?startNodeId=$start&destinationNodeId=$start";Assert ($same.Status -eq 200 -and $same.Body.totalCost -eq 0 -and $same.Body.points.Count -eq 1) 'Same-node API returns zero-length route'
 Assert ((Get 'api/maps/MISSING').Status -eq 404) 'Unknown floor returns 404'
 Assert ((Get "api/navigation/rooms/$([Guid]::NewGuid())/route?startNodeId=$start").Status -eq 404) 'Unmapped room returns 404'
 Assert ((Get "api/navigation/rooms/$r1/route?startNodeId=$([Guid]::NewGuid())").Status -eq 404) 'Unknown kiosk position returns 404'
 Assert ((Get "api/navigation/rooms/$r1/route?startNodeId=$([Guid]::Empty)").Status -eq 400) 'Empty start identifier returns 400'
 $null=Sql "INSERT INTO map_floors VALUES('60000000-0000-0000-0000-000000000099','F99','미연결 층',600,600);INSERT INTO map_nodes VALUES('61000000-0000-0000-0000-000000000099','60000000-0000-0000-0000-000000000099','미연결 복도','Corridor',100,100);"
 $cross=Get "api/navigation/routes?startNodeId=$start&destinationNodeId=61000000-0000-0000-0000-000000000099"
 Assert ($cross.Status -eq 409 -and $cross.Body.code -eq 'DIFFERENT_FLOOR') 'Unconnected floor route is explicitly rejected'
 $null=Sql "UPDATE map_nodes SET x=999 WHERE id='61000000-0000-0000-0000-000000000011';"
 $invalid=Get $path;Assert ($invalid.Status -eq 503 -and $invalid.Body.code -eq 'MAP_DATA_INVALID') 'Invalid drawing bounds fail without returning unsafe geometry'
 Assert ((Sql 'SELECT count(*) FROM visits;') -eq '0') 'Navigation does not create or modify registrations'
 Write-Output 'ALL STEP 8 API TESTS PASSED'
}finally{
 if($process -and -not $process.HasExited){Stop-Process -Id $process.Id;$process.WaitForExit()}
 docker compose exec -T postgres dropdb -U mediway --if-exists --force $database
 $env:ConnectionStrings__Postgres=$oldPg;$env:ConnectionStrings__Redis=$oldRedis;$env:QueueCache__KeyPrefix=$oldPrefix
 Pop-Location
}
