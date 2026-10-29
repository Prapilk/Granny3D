Add-Type -Path "C:\Games\Sphere\IrisGrannyViewer\UoFiddler\bin\Debug\OpenTK.Mathematics.dll"
Add-Type -Path "C:\Games\Sphere\IrisGrannyViewer\UoFiddler\bin\Debug\plugins\UoFiddler.Plugin.Granny3D.dll"

$charPath = "d:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\Superhero_Male_FullBody.char"
$model = [UoFiddler.Plugin.Granny3D.GrannyModelFile]::Load($charPath)
Write-Host "Model loaded: $($model.Name), Bones count: $($model.Bones.Count)"

$refPath = "d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn"
Write-Host "RefPath: $refPath, Exists: $(Test-Path $refPath)"

$parser = New-Object UoFiddler.Plugin.Granny3D.GrannyFormatParser
$parser.Load($refPath) | Out-Null
Write-Host "Ref bones count: $($parser.Bones.Count)"

foreach ($b in $model.Bones) {
    if ($b.Name -like "*Pelvis*" -or $b.Name -like "*Thigh*" -or $b.Name -like "*Calf*") {
        $rb = $parser.Bones | Where-Object { $_.Name -eq $b.Name }
        $curWorld = [OpenTK.Mathematics.Matrix4]::Invert($b.InverseBindMatrix)
        $refWorld = [OpenTK.Mathematics.Matrix4]::Invert($rb.InverseBindMatrix)
        
        $curRot = $curWorld.ExtractRotation()
        $refRot = $refWorld.ExtractRotation()
        
        $curMat = [OpenTK.Mathematics.Matrix4]::CreateFromQuaternion($curRot)
        $refMat = [OpenTK.Mathematics.Matrix4]::CreateFromQuaternion($refRot)
        $diff = [OpenTK.Mathematics.Matrix4]::Invert($refMat) * $curMat
        
        $sinY = -$diff.Row0.Z
        $rotY = [System.MathF]::Asin([System.Math]::Clamp($sinY, -1.0, 1.0))
        $cosY = [System.MathF]::Cos($rotY)
        $rotX = 0
        $rotZ = 0
        if ([System.MathF]::Abs($cosY) -gt 0.0001) {
            $rotX = [System.MathF]::Atan2($diff.Row1.Z, $diff.Row2.Z)
            $rotZ = [System.MathF]::Atan2($diff.Row0.Y, $diff.Row0.X)
        }
        $degX = [OpenTK.Mathematics.MathHelper]::RadiansToDegrees($rotX)
        $degY = [OpenTK.Mathematics.MathHelper]::RadiansToDegrees($rotY)
        $degZ = [OpenTK.Mathematics.MathHelper]::RadiansToDegrees($rotZ)
        
        Write-Host "Bone $($b.Name): Diff Euler = ($degX, $degY, $degZ)"
        Write-Host "   Char LocalPos = $($b.LocalPosition.X), $($b.LocalPosition.Y), $($b.LocalPosition.Z)"
        Write-Host "   Ref  LocalPos = $($rb.LocalPosition.X), $($rb.LocalPosition.Y), $($rb.LocalPosition.Z)"
    }
}
