# PowerShell 7. Owns temporary DB, Redis key prefix and API port 5094.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$database='mediway_ordertest_'+[Guid]::NewGuid().ToString('N');$process=$null
$oldPg=$env:ConnectionStrings__Postgres;$oldRedis=$env:ConnectionStrings__Redis;$oldPrefix=$env:QueueCache__KeyPrefix
Push-Location $root
try {
 $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,5094);try{$listener.Start()}finally{$listener.Stop()}
 docker compose exec -T postgres createdb -U mediway $database
 if($LASTEXITCODE -ne 0){throw 'Create test DB failed'}
 foreach($file in Get-ChildItem database/init/*.sql|Sort-Object Name){Get-Content $file.FullName -Raw|docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U mediway -d $database|Out-Null;if($LASTEXITCODE -ne 0){throw 'SQL failed'}}
 $line=Get-Content .env|Where-Object{$_ -match '^POSTGRES_PASSWORD='}|Select-Object -First 1
 $cs=[System.Data.Common.DbConnectionStringBuilder]::new();$cs['Host']='127.0.0.1';$cs['Port']=55432;$cs['Database']=$database;$cs['Username']='mediway';$cs['Password']=$line.Substring('POSTGRES_PASSWORD='.Length).Trim();$cs['Timeout']=3
 $env:ConnectionStrings__Postgres=$cs.ConnectionString;$env:ConnectionStrings__Redis='127.0.0.1:56379';$env:QueueCache__KeyPrefix=$database
 $dll=Join-Path $root 'src/MediWay.Api/bin/Debug/net10.0/MediWay.Api.dll'
 $process=Start-Process dotnet -ArgumentList @(('"'+$dll+'"'),'--urls','http://127.0.0.1:5094') -WindowStyle Hidden -PassThru
 $ready=$false
 for($i=0;$i -lt 40;$i++){if($process.HasExited){throw 'Test API exited'};try{$null=Invoke-RestMethod http://127.0.0.1:5094/health/live -TimeoutSec 1;$ready=$true;break}catch{Start-Sleep -Milliseconds 250}}
 if(-not $ready){throw 'Test API startup failed'}
 dotnet run --project tests/MediWay.Display.Tests --no-build -- --order-integration 'http://127.0.0.1:5094/'
 if($LASTEXITCODE -ne 0){throw 'Queue order integration tests failed'}
} finally {
 if($process -and -not $process.HasExited){Stop-Process -Id $process.Id;$process.WaitForExit()}
 $keys=@(docker compose exec -T redis redis-cli --scan --pattern "${database}:*")
 foreach($key in $keys){if(-not $key.StartsWith("${database}:")){throw 'Unexpected test key'};$null=docker compose exec -T redis redis-cli DEL $key}
 docker compose exec -T postgres dropdb -U mediway --if-exists --force $database
 $env:ConnectionStrings__Postgres=$oldPg;$env:ConnectionStrings__Redis=$oldRedis;$env:QueueCache__KeyPrefix=$oldPrefix
 Pop-Location
}
