param(
    [string]$ListenUrls = 'http://localhost:5080',
    [string]$MobileBaseUrl = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$envFile = Join-Path $projectRoot '.env'
if (-not (Test-Path -LiteralPath $envFile)) { throw '먼저 .env.example을 .env로 복사하고 비밀번호를 설정하세요.' }
$line = Get-Content -LiteralPath $envFile | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
if (-not $line) { throw '.env에 POSTGRES_PASSWORD가 없습니다.' }
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection['Host'] = 'localhost'; $connection['Port'] = 55432
$connection['Database'] = 'mediway'; $connection['Username'] = 'mediway'
$connection['Password'] = $line.Substring('POSTGRES_PASSWORD='.Length).Trim()
$connection['Timeout'] = 3
$previousPg = $env:ConnectionStrings__Postgres
$previousRedis = $env:ConnectionStrings__Redis
$previousMobile = $env:MobileGuide__PublicBaseUrl
$env:ConnectionStrings__Postgres = $connection.ConnectionString
$env:ConnectionStrings__Redis = '127.0.0.1:56379'
if ($MobileBaseUrl) { $env:MobileGuide__PublicBaseUrl = $MobileBaseUrl }
try {
    dotnet run --project (Join-Path $projectRoot 'src/MediWay.Api') --no-launch-profile --urls $ListenUrls
} finally {
    $env:ConnectionStrings__Postgres = $previousPg
    $env:ConnectionStrings__Redis = $previousRedis
    $env:MobileGuide__PublicBaseUrl = $previousMobile
}
