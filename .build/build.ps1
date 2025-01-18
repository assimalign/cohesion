# Import-Module -Name "$PSScriptRoot\scripts" -Force

# Invoke-Validation



$logs = Start-Process git  -ArgumentList('log', '--name-only')



git log --name-only --output="C:\Source\repos\assimalign-cohesion\cohesion\temp.txt"