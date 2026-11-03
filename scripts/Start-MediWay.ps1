# Supports Windows PowerShell 5.1 and PowerShell 7.
param(
 [ValidateSet('Kiosk','Staff','Display','All','None')][string]$Apps='Kiosk',
 [ValidateRange(1024,65535)][int]$Port=5080,
 [switch]$NoBuild,
 [switch]$CheckOnly
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$owned=[Collections.Generic.List[Diagnostics.Process]]::new()
$lock=$null
function Start-MediWayChild([string]$File,[string[]]$Arguments,[hashtable]$ChildEnvironment,[string]$LogName='') {
 $previous=@{}
 try {
  foreach($key in $ChildEnvironment.Keys){
   $previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process')
   [Environment]::SetEnvironmentVariable($key,$ChildEnvironment[$key],'Process')
  }
  $options=@{FilePath=$File;WorkingDirectory=$root;PassThru=$true}
  if($Arguments.Count -gt 0){$options.ArgumentList=$Arguments}
  if($LogName){
   $options.WindowStyle='Hidden'
   $options.RedirectStandardOutput=Join-Path $runtime "$LogName.log"
   $options.RedirectStandardError=Join-Path $runtime "$LogName-error.log"
  }else{$options.WindowStyle='Normal'}
  Start-Process @options
 }finally{
  foreach($key in $previous.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key],'Process')}
 }
}
Push-Location $root
try {
 if($PSVersionTable.PSVersion -lt [version]'5.1'){throw 'PowerShell 5.1 이상에서 실행하세요.'}
 foreach($command in @('docker','dotnet')){if(-not(Get-Command $command -ErrorAction SilentlyContinue)){throw "$command 실행 파일이 필요합니다."}}
 if(-not(Test-Path -LiteralPath '.env')){throw '.env에 기존 PostgreSQL 비밀번호를 설정한 뒤 실행하세요. README 실행 절차를 참고하세요.'}
 $runtime=Join-Path $root '.runtime';$null=New-Item -ItemType Directory -Force $runtime
 $lock=[IO.FileStream]::new((Join-Path $runtime 'launcher.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 $null=docker info --format '{{.ServerVersion}}' 2>$null
 if($LASTEXITCODE -ne 0){
  $desktop=Join-Path $env:ProgramFiles 'Docker/Docker/Docker Desktop.exe'
  if(-not(Test-Path -LiteralPath $desktop)){throw 'Docker Desktop을 설치하고 Linux 컨테이너 엔진을 실행하세요.'}
  Write-Output 'Docker Desktop 시작 중...'
  Start-Process -FilePath $desktop -WindowStyle Hidden
  $ready=$false
  for($i=0;$i -lt 30;$i++){
   $null=docker info --format '{{.ServerVersion}}' 2>$null
   if($LASTEXITCODE -eq 0){$ready=$true;break}
   Start-Sleep -Seconds 2
  }
  if(-not $ready){throw 'Docker 엔진을 시작하지 못했습니다. Docker Desktop 오류 화면을 확인하세요.'}
 }
 docker compose up -d --wait --wait-timeout 60
 if($LASTEXITCODE -ne 0){throw 'PostgreSQL·Redis 준비에 실패했습니다.'}
 $schema=docker compose exec -T postgres psql -U mediway -d mediway -At -c "SELECT to_regclass('visit_requests') IS NOT NULL AND to_regclass('map_nodes') IS NOT NULL AND to_regclass('queue_order_requests') IS NOT NULL AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='visits' AND column_name='queue_priority') AND to_regclass('map_floor_connections') IS NOT NULL AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='rooms' AND column_name='floor_id') AND to_regclass('ux_visits_active_patient_hospital') IS NOT NULL;"
 if($LASTEXITCODE -ne 0 -or $schema -ne 't'){throw '기존 DB 스키마를 확인하세요. README의 003·004·005·006·007 SQL 적용 절차가 필요합니다.'}
 if(-not $NoBuild){dotnet build;if($LASTEXITCODE -ne 0){throw '빌드 실패: 실행 중인 WPF 앱이 있다면 종료 후 다시 시도하세요.'}}
 $base="http://localhost:$Port/"
 $existing=$null
 try{$existing=Invoke-RestMethod "${base}health/live" -TimeoutSec 2}catch{}
 if($existing){
  if($existing.application -ne 'MEDI-WAY'){throw '해당 포트의 서버가 MEDI-WAY인지 확인할 수 없습니다. 다른 포트를 사용하세요.'}
  Write-Output "기존 MEDI-WAY API 사용: $base"
 }else{
  $probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$Port)
  try{$probe.Start()}finally{$probe.Stop()}
  $line=Get-Content .env|Where-Object{$_ -match '^POSTGRES_PASSWORD='}|Select-Object -First 1
  if(-not $line){throw 'POSTGRES_PASSWORD 설정이 없습니다.'}
  $cs=[Data.Common.DbConnectionStringBuilder]::new();$cs['Host']='127.0.0.1';$cs['Port']=55432;$cs['Database']='mediway';$cs['Username']='mediway';$cs['Password']=$line.Substring('POSTGRES_PASSWORD='.Length).Trim();$cs['Timeout']=3
  $dll=Join-Path $root 'src/MediWay.Api/bin/Debug/net10.0/MediWay.Api.dll'
  if(-not(Test-Path -LiteralPath $dll)){throw 'API 빌드 파일이 없습니다.'}
  $api=Start-MediWayChild -File 'dotnet' -Arguments @(('"'+$dll+'"'),'--urls',$base) -ChildEnvironment @{
   ConnectionStrings__Postgres=$cs.ConnectionString;ConnectionStrings__Redis='127.0.0.1:56379';QueueCache__KeyPrefix='mediway';MobileGuide__PublicBaseUrl=$base
  } -LogName 'api'
  $owned.Add($api)
 }
 $healthy=$false
 for($i=0;$i -lt 20;$i++){
  try{$health=Invoke-RestMethod "${base}health/ready" -TimeoutSec 3;if($health.status -eq 'Healthy'){$healthy=$true;break}}catch{}
  Start-Sleep -Milliseconds 300
 }
 if(-not $healthy){throw 'API·PostgreSQL·Redis 연결 확인 실패. .runtime/api-error.log와 Docker 상태를 확인하세요.'}
 Write-Output "준비 완료: API·PostgreSQL·Redis 정상 ($base)"
 if($CheckOnly){Write-Output 'CHECK PASSED: 저장소·API 확인 완료. 테스트로 시작한 API를 정리합니다.';return}
 $selected=if($Apps -eq 'All'){@('Kiosk','Staff','Display')}elseif($Apps -eq 'None'){@()}else{@($Apps)}
 foreach($app in $selected){
  $exe=Join-Path $root "src/MediWay.$app/bin/Debug/net10.0-windows/MediWay.$app.exe"
  if(-not(Test-Path -LiteralPath $exe)){throw "$app 빌드 파일이 없습니다."}
  # Interactive WPF windows must be visible; API remains hidden.
  $child=Start-MediWayChild -File $exe -Arguments @() -ChildEnvironment @{MEDIWAY_API_BASE_URL=$base}
  $owned.Add($child)
  Write-Output "$app 실행 완료"
 }
 Write-Output '이 터미널을 유지하세요. Enter를 누르면 이번에 시작한 앱과 API를 종료합니다. Docker 저장소는 유지합니다.'
 $null=Read-Host
}finally{
 for($i=$owned.Count-1;$i -ge 0;$i--){
  $child=$owned[$i]
  if(-not $child.HasExited){if($child.CloseMainWindow()){$null=$child.WaitForExit(5000)};if(-not $child.HasExited){$child.Kill();$child.WaitForExit()}}
  $child.Dispose()
 }
 if($lock){$lock.Dispose()}
 Pop-Location
}
