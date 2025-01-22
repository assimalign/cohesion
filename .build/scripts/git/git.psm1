function Get-GitConfig(
    [string]$Path
) {
    if ((Test-Path $Path) -eq $false -and $Path.EndsWith('.git/config')) {
        Write-Error "The provided path either does not exist or is an invalid .git config file"
        return
    }
    # Split Config File into separate lines
    $lines = (Get-Content -Path $Path)
    $config = @{}

    for ($i = 0; $i -lt $lines.Length; $i++) {
        # All Git Configs values have an indent
        $header = $lines[$i].TrimStart('[').TrimEnd(']').Split(' ')
        $header_key = (Get-Culture).TextInfo.ToTitleCase(($header[0] -Replace '[^0-9A-Z]', ' ')) -Replace ' '

        # Parse Core Configuration
        if ($header_key -eq 'core') {
            $i++
            while ($null -ne $lines[$i] -and $lines[$i].StartsWith("`t")) {
                $line = $lines[$i].Trim("`t").Split(' = ')
                # Convert to Pascal Case
                $text = $line[0] -Replace '[^0-9A-Z]', ' '
                $key = (Get-Culture).TextInfo.ToTitleCase($text) -Replace ' '
                $value = $line[1]
                $config.Add($key, $value)
                # Check if the next line is a header
                if ($null -ne $lines[$i + 1] -and $lines[$i + 1].StartsWith("`t") -ne $true) {
                    break
                }
                $i++
            }
        }
        # Parse Git Configuration
        elseif ($null -ne $header_key -and $null -ne $header[1]) {
            if ($config.ContainsKey($header_key) -eq $false) {
                $config.Add($header_key, @{})
            }
            $i++
            $header_value = (Get-Culture).TextInfo.ToTitleCase(($header[1].Trim('"') -Replace '[^0-9A-Z]', ' ')) -Replace ' '
            $configs = @{}
            while ($null -ne $lines[$i] -and $lines[$i].StartsWith("`t")) {
                $line = $lines[$i].Trim("`t").Split(' = ')
                # Convert to Pascal Case
                $text = $line[0] -Replace '[^0-9A-Z]', ' '
                $key = (Get-Culture).TextInfo.ToTitleCase($text) -Replace ' '
                $value = $line[1]
                
                $configs.Add($key, $value)
                if ($null -ne $lines[$i + 1] -and $lines[$i + 1].StartsWith("`t") -ne $true) {
                    break
                }
                $i++
            }
            $config[$header_key].Add($header_value, $configs)  
        }
    }
    return $config
}


function Get-GitFileChanges {


    $logs = Start-Process git  -ArgumentList('log', '--name-only')  
}