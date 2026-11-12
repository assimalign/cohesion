# Read-only oracle capture. Start/stop the PostgreSQL 17 container separately.
param([string] $Container = 'ix-t1-oracle', [string] $Revision = 'c9ac0df64', [string] $OutputPath = "$PSScriptRoot/EnglishPostgres17.tsv")
$ErrorActionPreference = 'Stop'
$repository = Resolve-Path "$PSScriptRoot/../../../../.."
$words = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
$lines = @(& git -C $repository grep -h -I -E '.' $Revision -- ':(glob)docs/**/*.md' ':(glob)resources/Database/**/*.md')
if ($LASTEXITCODE -ne 0) { throw 'Could not read the pinned documentation revision.' }
foreach ($line in $lines) {
    foreach ($match in [regex]::Matches($line, '[A-Za-z]{2,}')) {
        [void]$words.Add($match.Value.ToLowerInvariant())
    }
}
$bases = @($words | Where-Object { $_.Length -ge 3 -and $_.Length -le 15 } | Select-Object -First 700)
$suffixes = @('s','sses','ies','ied','ss','us','eed','eedly','ed','edly','ing','ingly','y','ization','ational','fulness','ousness','iveness','tional','biliti','lessli','entli','ation','alism','aliti','ousli','iviti','fulli','enci','anci','abli','izer','ator','alli','bli','ogi','li','alize','icate','iciti','ative','ical','ness','ful','ement','ance','ence','able','ible','ment','ant','ent','ism','ate','iti','ous','ive','ize','ion','al','er','ic','e','ll')
foreach ($base in $bases) {
    foreach ($suffix in $suffixes) { [void]$words.Add($base + $suffix) }
}
foreach ($word in @('skis','skies','dying','lying','tying','idly','gently','ugly','early','only','singly','sky','news','howe','atlas','cosmos','bias','andes','inning','outing','canning','herring','earring','proceed','exceed','succeed',"'yawning","dog's","dogs'","dog's'",'cry','happy','boy','generously','communally','arsenal','a𐐨ing','𐐨aing','caféing','élational')) { [void]$words.Add($word) }
$query = "CREATE TEMP TABLE words(word text);`nCOPY words FROM STDIN;`n" + ($words -join "`n") + "`n\.`nSELECT word, coalesce((ts_lexize('english_stem',word))[1],'') FROM words ORDER BY word COLLATE `"C`";"
$result = @($query | & podman exec -i $Container psql -X -q -t -A -F "`t" -U postgres)
if ($LASTEXITCODE -ne 0) { throw 'Oracle query failed.' }
if ($result.Count -lt 20000) { throw 'Oracle corpus must contain at least 20,000 distinct words.' }
[IO.File]::WriteAllLines([IO.Path]::GetFullPath($OutputPath), $result, [Text.UTF8Encoding]::new($false))
Write-Output "Captured $($result.Count) distinct PostgreSQL 17 english_stem pairs. Empty results are stop words."
