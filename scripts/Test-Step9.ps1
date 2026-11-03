# PowerShell 7. Owns temporary DB, dedicated Redis and API port 5090.
$ErrorActionPreference='Stop';$root=Split-Path $PSScriptRoot -Parent
$suffix=[Guid]::NewGuid().ToString('N');$database='mediway_mobiletest_'+$suffix;$container='mediway-mobiletest-'+$suffix;$process=$null
$oldPg=$env:ConnectionStrings__Postgres;$oldRedis=$env:ConnectionStrings__Redis;$oldPrefix=$env:QueueCache__KeyPrefix;$oldBase=$env:MobileGuide__PublicBaseUrl
Push-Location $root
try{
 $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,5090);try{$listener.Start()}finally{$listener.Stop()}
 docker compose exec -T postgres createdb -U mediway $database;if($LASTEXITCODE -ne 0){throw 'Create DB failed'}
 foreach($file in Get-ChildItem database/init/*.sql|Sort-Object Name){Get-Content $file.FullName -Raw|docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U mediway -d $database|Out-Null;if($LASTEXITCODE -ne 0){throw 'SQL failed'}}
 $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
 $null=docker run -d --name $container -p "127.0.0.1:${port}:6379" redis:7.4;if($LASTEXITCODE -ne 0){throw 'Redis startup failed'}
 $line=Get-Content .env|Where-Object{$_ -match '^POSTGRES_PASSWORD='}|Select-Object -First 1
 $cs=[System.Data.Common.DbConnectionStringBuilder]::new();$cs['Host']='127.0.0.1';$cs['Port']=55432;$cs['Database']=$database;$cs['Username']='mediway';$cs['Password']=$line.Substring('POSTGRES_PASSWORD='.Length).Trim();$cs['Timeout']=3
 $env:ConnectionStrings__Postgres=$cs.ConnectionString;$env:ConnectionStrings__Redis="127.0.0.1:$port";$env:QueueCache__KeyPrefix=$database;$env:MobileGuide__PublicBaseUrl='http://127.0.0.1:5090/'
 $dll=Join-Path $root 'src/MediWay.Api/bin/Debug/net10.0/MediWay.Api.dll'
 $process=Start-Process dotnet -ArgumentList @(('"'+$dll+'"'),'--urls','http://127.0.0.1:5090') -WindowStyle Hidden -PassThru
 $started=$false;for($i=0;$i -lt 40;$i++){if($process.HasExited){throw 'API exited during startup'};try{$null=Invoke-RestMethod http://127.0.0.1:5090/health/live -TimeoutSec 1;$started=$true;break}catch{Start-Sleep -Milliseconds 250}};if(-not $started){throw 'API startup failed'}
 dotnet run --project tests/MediWay.MobileGuide.Tests --no-build -- 'http://127.0.0.1:5090/' $port $container $database
 if($LASTEXITCODE -ne 0){throw 'Mobile guide tests failed'}
}finally{
 if($process -and -not $process.HasExited){Stop-Process -Id $process.Id;$process.WaitForExit()}
 $null=docker rm -f $container
 docker compose exec -T postgres dropdb -U mediway --if-exists --force $database
 $env:ConnectionStrings__Postgres=$oldPg;$env:ConnectionStrings__Redis=$oldRedis;$env:QueueCache__KeyPrefix=$oldPrefix;$env:MobileGuide__PublicBaseUrl=$oldBase
 Pop-Location
}
