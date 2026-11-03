# PowerShell 7. Test harness owns and restarts API 5096; no Windows firewall changes.
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
$database='mediway_offlinetest_'+[Guid]::NewGuid().ToString('N')
$oldPg=$env:ConnectionStrings__Postgres;$oldRedis=$env:ConnectionStrings__Redis;$oldPrefix=$env:QueueCache__KeyPrefix
Push-Location $root
try {
 docker compose exec -T postgres createdb -U mediway $database
 if($LASTEXITCODE -ne 0){throw 'Create test DB failed'}
 foreach($file in Get-ChildItem database/init/*.sql|Sort-Object Name){Get-Content $file.FullName -Raw|docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U mediway -d $database|Out-Null;if($LASTEXITCODE -ne 0){throw 'SQL failed'}}
 $line=Get-Content .env|Where-Object{$_ -match '^POSTGRES_PASSWORD='}|Select-Object -First 1
 $cs=[System.Data.Common.DbConnectionStringBuilder]::new();$cs['Host']='127.0.0.1';$cs['Port']=55432;$cs['Database']=$database;$cs['Username']='mediway';$cs['Password']=$line.Substring('POSTGRES_PASSWORD='.Length).Trim();$cs['Timeout']=3
 $env:ConnectionStrings__Postgres=$cs.ConnectionString;$env:ConnectionStrings__Redis='127.0.0.1:56379';$env:QueueCache__KeyPrefix=$database
 dotnet run --project tests/MediWay.Kiosk.Tests --no-build -- --offline-integration $root 'http://127.0.0.1:5096/'
 if($LASTEXITCODE -ne 0){throw 'Offline integration tests failed'}
} finally {
 $keys=@(docker compose exec -T redis redis-cli --scan --pattern "${database}:*")
 foreach($key in $keys){if(-not $key.StartsWith("${database}:")){throw 'Unexpected test key'};$null=docker compose exec -T redis redis-cli DEL $key}
 docker compose exec -T postgres dropdb -U mediway --if-exists --force $database
 $env:ConnectionStrings__Postgres=$oldPg;$env:ConnectionStrings__Redis=$oldRedis;$env:QueueCache__KeyPrefix=$oldPrefix
 Pop-Location
}
