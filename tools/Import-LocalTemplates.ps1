param([string]$RawRoot = 'D:\C07 MODDING\Crickt_07_Raw_Files\players\heads')
$ErrorActionPreference = 'Stop'
$studioRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rawRoot = $RawRoot
$rows = [Collections.Generic.List[object]]::new()
function Read-BigEndian([byte[]]$data, [int]$offset) {
    return [int]([uint32]$data[$offset] * 16777216 + [uint32]$data[$offset+1] * 65536 + [uint32]$data[$offset+2] * 256 + [uint32]$data[$offset+3])
}
# Stock heads with an extra mesh textured 'hair', checked by rendering it alone: full hair meshes, and two with only a
# front fringe (the rest is painted). Left out: 2786 (its 'hair' part is glasses), 4666 (turban), 45001 (shared
# created-player texture not in its own FSH).
$withHair = @('14360','1536','23525','28116','6059','8881')
$withFringe = @('15484','9324')
foreach ($id in @('1188','10942','21449','10407') + $withHair + $withFringe) {
    $source = Join-Path $rawRoot ($id + '.big')
    if (-not (Test-Path -LiteralPath $source)) { continue }
    $data = [IO.File]::ReadAllBytes($source)
    if ([Text.Encoding]::ASCII.GetString($data,0,4) -ne 'BIGF') { throw 'Unsupported face archive' }
    $count = Read-BigEndian $data 8
    $cursor = 16
    $dest = Join-Path $studioRoot "templates\heads\$id"
    New-Item -ItemType Directory -Force $dest | Out-Null
    for ($i=0; $i -lt $count; $i++) {
        $offset = Read-BigEndian $data $cursor
        $length = Read-BigEndian $data ($cursor+4)
        $cursor += 8
        $end = [Array]::IndexOf($data, [byte]0, $cursor)
        if ($end -lt 0 -or $offset -lt 0 -or $length -lt 0 -or $offset+$length -gt $data.Length) { throw 'Invalid archive bounds' }
        $name = [Text.Encoding]::ASCII.GetString($data,$cursor,$end-$cursor)
        $cursor = $end+1
        if ($name -eq 'model.o' -or $name -eq 'textures' -or $name -eq 'textures.fsh') {
            $outName = if ($name -eq 'model.o') { 'model.o' } else { 'textures.fsh' }
            [IO.File]::WriteAllBytes((Join-Path $dest $outName), $data[$offset..($offset+$length-1)])
        }
    }
    $label = if ($withHair -contains $id) { "Stock head $id " + [char]0x00B7 + " with hair" } elseif ($withFringe -contains $id) { "Stock head $id " + [char]0x00B7 + " with hair fringe" } else { "Stock head $id" }
    $rows.Add([ordered]@{Name=$label;Category='Head';Model="heads/$id/model.o";Texture="heads/$id/textures.fsh";Source=('players\heads\' + $id + '.big');Sha256=(Get-FileHash (Join-Path $dest 'model.o')).Hash;HasHair=($withHair + $withFringe) -contains $id})
}
New-Item -ItemType Directory -Force (Join-Path $studioRoot 'templates\hair') | Out-Null
foreach ($hair in Get-ChildItem (Join-Path $rawRoot 'gameface\hhr*.o')) {
    Copy-Item -LiteralPath $hair.FullName -Destination (Join-Path $studioRoot 'templates\hair')
    $rows.Add([ordered]@{Name=('Hair '+$hair.BaseName);Category='Hair';Model=('hair/'+$hair.Name);Texture=$null;Source=('players\heads\gameface\' + $hair.Name);Sha256=(Get-FileHash $hair.FullName).Hash})
}
$rows | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $studioRoot 'templates\catalog.json') # names contain non-ASCII (·)
Write-Output "Imported $($rows.Count) local templates."
