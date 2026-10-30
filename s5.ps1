$p = 'C:\Users\RiyadhSarker\AppData\Local\orchestra-node\repos\packman\.orch\qa-output\Packman-QA-Orchestra_LinkTest\1.0.0\Intune'
Get-ChildItem $p | Select-Object Name,Length,LastWriteTime | Format-Table -AutoSize
'--- packman.id ---'
Get-Content "$p\packman.id"
'--- keys ---'
((Get-Content "$p\packman.id" -Raw | ConvertFrom-Json).PSObject.Properties.Name) -join ','
