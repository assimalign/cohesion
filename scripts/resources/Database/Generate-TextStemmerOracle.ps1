<#!
.SYNOPSIS
Captures the English stemmer and parser fixtures from PostgreSQL 17.11.
.DESCRIPTION
Start a postgres:17.11 container named t3-fx-ix-t1-oracle separately. This script
checks the server version and queries it read-only; it does not manage containers.
The pinned repository revision supplies vocabulary, with suffix bases spread over
its ordinal-sorted vocabulary by deterministic stride. Tests need no running server.
!#>
param([string] $Container = 't3-fx-ix-t1-oracle', [string] $Revision = 'c9ac0df64', [string] $OutputPath = "$PSScriptRoot/../../../resources/Database/Assimalign.Cohesion.Database.Text/tests/TestObjects/EnglishPostgres17.tsv")
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)
$version = & podman exec $Container psql -X -U postgres -Atc 'SHOW server_version'
if ($LASTEXITCODE -ne 0 -or !$version.StartsWith('17.11')) { throw "Expected PostgreSQL 17.11, got $version" }
$repository = Resolve-Path "$PSScriptRoot/../../.."
$words = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
$lines = @(& git -C $repository grep -h -I -E '.' $Revision -- ':(glob)docs/**/*.md' ':(glob)resources/Database/**/*.md')
if ($LASTEXITCODE -ne 0) { throw 'Could not read the pinned documentation revision.' }
foreach ($line in $lines) {
    foreach ($match in [regex]::Matches($line, '[A-Za-z]{2,}')) {
        [void]$words.Add($match.Value.ToLowerInvariant())
    }
}
$eligible = @($words | Where-Object { $_.Length -ge 3 -and $_.Length -le 15 })
# Evenly spaced indices span the entire ordinal-sorted vocabulary.
$bases = for ($index = 0; $index -lt 700; $index++) { $eligible[[int][Math]::Floor($index * ($eligible.Count - 1) / 699)] }
$suffixes = @('s','sses','ies','ied','ss','us','eed','eedly','ed','edly','ing','ingly','y','ization','ational','fulness','ousness','iveness','tional','biliti','lessli','entli','ation','alism','aliti','ousli','iviti','fulli','enci','anci','abli','izer','ator','alli','bli','ogi','li','alize','icate','iciti','ative','ical','ness','ful','ement','ance','ence','able','ible','ment','ant','ent','ism','ate','iti','ous','ive','ize','ion','al','er','ic','e','ll')
foreach ($base in $bases) {
    foreach ($suffix in $suffixes) { [void]$words.Add($base + $suffix) }
}
foreach ($word in @('skis','skies','dying','lying','tying','idly','gently','ugly','early','only','singly','sky','news','howe','atlas','cosmos','bias','andes','inning','outing','canning','herring','earring','proceed','exceed','succeed',"'yawning","dog's","dogs'","dog's'",'cry','happy','boy','generously','communally','arsenal','a𐐨ing','𐐨aing','caféing','élational')) { [void]$words.Add($word) }
foreach ($stop in Get-Content (Join-Path $repository 'resources/Database/Assimalign.Cohesion.Database.Text/tests/TestObjects/EnglishStopWords127.tsv')) { [void]$words.Add($stop) }
[void]$words.Add("''s")
$query = "CREATE TEMP TABLE words(word text);`nCOPY words FROM STDIN;`n" + ($words -join "`n") + "`n\.`nSELECT word, coalesce((ts_lexize('english_stem',word))[1],'{}') FROM words ORDER BY word COLLATE `"C`";"
$result = @($query | & podman exec -i $Container psql -X -q -t -A -F "`t" -U postgres)
if ($LASTEXITCODE -ne 0) { throw 'Oracle query failed.' }
if ($result.Count -lt 20000) { throw 'Oracle corpus must contain at least 20,000 distinct words.' }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($result -join "`r`n") + "`r`n",[Text.UTF8Encoding]::new($false))
Write-Output "Captured $($result.Count) total and $($words.Count) distinct PostgreSQL 17.11 english_stem pairs."
$words | Group-Object { $_.Substring(0, $(if ([char]::IsHighSurrogate($_[0])) { 2 } else { 1 })) } | Sort-Object Name | ForEach-Object { Write-Output "Initial $($_.Name): $($_.Count)" }
$parserPath = Join-Path $repository 'resources/Database/Assimalign.Cohesion.Database.Text/tests/TestObjects/ParserPostgres17.tsv'
$parserRows = [Collections.Generic.List[string]]::new()
foreach ($line in Get-Content -Encoding utf8 $parserPath) {
    $parts = $line.Split("`t")
    $literal = $parts[1].Replace("'", "''")
    $sql = "SELECT coalesce(nullif(to_tsvector('simple','$literal')::text,''),'{}'), coalesce(nullif(to_tsvector('english','$literal')::text,''),'{}');"
    $observed = @($sql | & podman exec -i $Container psql -X -U postgres -q -t -A -F "`t")
    if ($LASTEXITCODE -ne 0 -or $observed.Count -ne 1) { throw "Parser query failed for $($parts[0])" }
    $columns = $observed[0].Split("`t")
    $parts[2] = $columns[0]; $parts[3] = $columns[1]
    $parserRows.Add(($parts -join "`t"))
}
[IO.File]::WriteAllText($parserPath,($parserRows -join "`r`n") + "`r`n",[Text.UTF8Encoding]::new($false))
$vectorRows = [Collections.Generic.List[string]]::new()
$prefix = "'x':" + ((1..255) -join ',') + ','
foreach ($case in @(
    @{ Name = 'count-cutoff'; Input = 'x:' + ((1..255) -join ',') + ' x:256B,256A'; Expected = $prefix + '256A' },
    @{ Name = 'position-cutoff'; Input = 'x:16383A,16383B,1,2,3,4,5'; Expected = "'x':1,2,3,4,5,16383A" }
)) {
    $sql = "SELECT '$($case.Input)'::tsvector;"
    $observed = @($sql | & podman exec -i $Container psql -X -U postgres -q -t -A)
    if ($LASTEXITCODE -ne 0 -or $observed.Count -ne 1) { throw 'Vector query failed.' }
    $vectorRows.Add((@($case.Name, $case.Input, $observed[0], $case.Expected) -join "`t"))
}
$vectorPath = Join-Path (Split-Path $parserPath) 'VectorPostgres17.tsv'
[IO.File]::WriteAllText($vectorPath,($vectorRows -join "`r`n") + "`r`n",[Text.UTF8Encoding]::new($false))
