param([string]$Before,[string]$After)
$ErrorActionPreference='Stop'
function Read-Attribute($g,[int]$index){
 $a=$g.accessors[$index];$v=$g.bufferViews[$a.bufferView];$b=[Convert]::FromBase64String($g.buffers[$v.buffer].uri.Split(',')[1])
 $dim=@{SCALAR=1;VEC2=2;VEC3=3;VEC4=4}[$a.type];$size=@{5126=4;5125=4;5123=2;5121=1}[[int]$a.componentType]
 $stride=if($v.byteStride){$v.byteStride}else{$dim*$size};$start=[int]$v.byteOffset+[int]$a.byteOffset
 $values=[Collections.Generic.List[double]]::new()
 for($i=0;$i -lt $a.count;$i++){for($c=0;$c -lt $dim;$c++){
  $o=$start+$i*$stride+$c*$size
  $value=switch($a.componentType){5126{[BitConverter]::ToSingle($b,$o)}5125{[BitConverter]::ToUInt32($b,$o)}5123{[BitConverter]::ToUInt16($b,$o)}5121{$b[$o]}}
  if($a.normalized){if($a.componentType -eq 5121){$value=$value/255.0}elseif($a.componentType -eq 5123){$value=$value/65535.0}}
  $values.Add($value)
 }}
 return ,$values.ToArray()
}
$a=Get-Content -LiteralPath $Before -Raw|ConvertFrom-Json;$b=Get-Content -LiteralPath $After -Raw|ConvertFrom-Json
foreach($name in @('COLOR_0','JOINTS_0','WEIGHTS_0','POSITION','TEXCOORD_0')){
 $x=Read-Attribute $a $a.meshes[0].primitives[0].attributes.$name;$y=Read-Attribute $b $b.meshes[0].primitives[0].attributes.$name
 $diffs=@(for($i=0;$i -lt [Math]::Min($x.Length,$y.Length);$i++){if($x[$i] -ne $y[$i]){[pscustomobject]@{Index=$i;Before=$x[$i];After=$y[$i];Difference=[Math]::Abs($x[$i]-$y[$i])}}})
 Write-Output "$name differences=$($diffs.Count) count=$($x.Length)/$($y.Length)"
 $diffs|Sort-Object Difference -Descending|Select-Object -First 5
}
