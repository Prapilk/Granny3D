$asm = [System.Reflection.Assembly]::LoadFrom('C:\Games\Sphere\IrisGrannyViewer\TestPlugin.dll')
try {
    $types = $asm.GetExportedTypes()
    Write-Host "Exported types: $($types.Count)"
    $types | Where-Object { $_.Name -like '*Granny*' } | Select-Object FullName
} catch {
    Write-Host "Error: $($_.Exception.Message)"
    if ($_.Exception.LoaderExceptions) {
        $_.Exception.LoaderExceptions | ForEach-Object { Write-Host "  $($_.Message)" }
    }
}