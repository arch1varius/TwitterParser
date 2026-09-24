param(
    [string]$PostgresBin = 'C:\Program Files\PostgreSQL\17\bin',
    [int]$Port = 15439
)
$ErrorActionPreference = 'Stop'
$parserWorkspace = Split-Path -Parent $PSScriptRoot
$parserTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('twitter-parser-tests-' + [Guid]::NewGuid().ToString('N'))
$parserData = Join-Path $parserTestRoot 'data'
$previousConnection = $env:PARSER_TEST_CONNECTION
$started = $false
try {
    New-Item -ItemType Directory -Path $parserTestRoot | Out-Null
    & (Join-Path $PostgresBin 'initdb.exe') -D $parserData -U parser_tests -A trust --no-locale --encoding=UTF8
    if ($LASTEXITCODE -ne 0) { throw 'initdb failed' }
    & (Join-Path $PostgresBin 'pg_ctl.exe') -D $parserData -l (Join-Path $parserTestRoot 'server.log') -o "-h 127.0.0.1 -p $Port" -w start
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL failed to start; see $parserTestRoot\server.log" }
    $started = $true
    $env:PARSER_TEST_CONNECTION = "Host=127.0.0.1;Port=$Port;Database=postgres;Username=parser_tests"
    & dotnet test (Join-Path $parserWorkspace 'tests\Parser.Tests\Parser.Tests.csproj') -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}
finally {
    $env:PARSER_TEST_CONNECTION = $previousConnection
    if ($started) { & (Join-Path $PostgresBin 'pg_ctl.exe') -D $parserData -m fast -w stop }
    # Keep the isolated cluster and logs for diagnostics; no existing databases are touched.
    Write-Host "Test data and logs: $parserTestRoot"
}
