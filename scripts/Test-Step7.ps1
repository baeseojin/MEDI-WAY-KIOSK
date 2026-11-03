# PowerShell 7; the console harness owns and restarts a test API on 5086.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$database='mediway_displaytest_'+[Guid]::NewGuid().ToString('N')
$oldPg=$env:ConnectionStrings__Postgres;$oldRedis=$env:ConnectionStrings__Redis;$oldPrefix=$env:QueueCache__KeyPrefix
Push-Location $root
try {
 docker compose exec -T postgres createdb -U mediway $database
 if($LASTEXITCODE -ne 0){throw 'Could not create temporary DB'}
 foreach($file in Get-ChildItem database/init/*.sql|Sort-Object Name){Get-Content $file.FullName -Raw|docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U mediway -d $database|Out-Null;if($LASTEXITCODE -ne 0){throw 'SQL failed'}}
 $line=Get-Content .env|Where-Object{$_ -match '^POSTGRES_PASSWORD='}|Select-Object -First 1
 $cs=[System.Data.Common.DbConnectionStringBuilder]::new();$cs['Host']='127.0.0.1';$cs['Port']=55432;$cs['Database']=$database;$cs['Username']='mediway';$cs['Password']=$line.Substring('POSTGRES_PASSWORD='.Length).Trim();$cs['Timeout']=3
 $env:ConnectionStrings__Postgres=$cs.ConnectionString;$env:ConnectionStrings__Redis='127.0.0.1:56379';$env:QueueCache__KeyPrefix=$database
 dotnet run --project tests/MediWay.Display.Tests --no-build -- --integration $root 'http://127.0.0.1:5086/'
 if($LASTEXITCODE -ne 0){throw 'DID integration tests failed'}
} finally {
 docker compose exec -T postgres dropdb -U mediway --if-exists --force $database
 $env:ConnectionStrings__Postgres=$oldPg;$env:ConnectionStrings__Redis=$oldRedis;$env:QueueCache__KeyPrefix=$oldPrefix
 Pop-Location
}
