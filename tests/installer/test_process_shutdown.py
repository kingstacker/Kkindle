"""Exercise the actual embedded installer script with mocked process APIs.
Run on Windows: python tests/installer/test_process_shutdown.py
"""
from pathlib import Path
import re, subprocess, tempfile, os
source=(Path(__file__).resolve().parents[2] / 'installer/Kkindle.iss').read_text(encoding='utf-8-sig')
block=source.split('  Script :=\n',1)[1].split('\n\n  if not SaveStringToFile',1)[0]
script='\n'.join(re.match(r"\s*'((?:[^']|'')*)'",line).group(1).replace("''", "'") for line in block.splitlines())
ps=os.path.join(os.environ['SystemRoot'],'System32','WindowsPowerShell','v1.0','powershell.exe')
for scenario,expected,stopped in [('normal',0,'11,12'),('fallback',0,'11,12'),('unknown',1,'11,12'),('gone',0,'11,12'),('refused',1,'11,12')]:
 with tempfile.TemporaryDirectory() as d:
  log=Path(d)/'stopped.txt'
  pre=r'''
param([string]$root)
$script:round=0
function Get-CimInstance {
 $script:round++
 $items=@([pscustomobject]@{ProcessId=99;ExecutablePath='C:\other\mcp\Kkindle.McpServer.exe'})
 if ($script:round -eq 1 -or $scenario -eq 'refused') {
  $items += [pscustomobject]@{ProcessId=11;ExecutablePath=(Join-Path $root 'Kkindle.exe')}
  $items += [pscustomobject]@{ProcessId=12;ExecutablePath=$(if($scenario -eq 'fallback'){$null}else{Join-Path $root 'mcp\Kkindle.McpServer.exe'})}
 }
 if ($scenario -in @('unknown','gone')) {$items = @([pscustomobject]@{ProcessId=88;ExecutablePath=$null}) + $items}
 return $items
}
function Get-Process {
 param($Id)
 if ($Id -eq 12) {return [pscustomobject]@{Path=(Join-Path $root 'mcp\Kkindle.McpServer.exe')}}
 if ($scenario -eq 'unknown') {return [pscustomobject]@{Path=$null}}
 return $null
}
function Stop-Process {
 param($Id,[switch]$Force)
 if ($Id -eq 99 -or $Id -eq 88) {throw 'Stopped unrelated process'}
 Add-Content -LiteralPath $log -Value $Id
 if ($scenario -eq 'refused') {throw 'Access denied'}
}
'''
  body=script.replace('$args[0]','$root').replace('AddSeconds(10)','AddMilliseconds(450)')
  file=Path(d)/'test.ps1'
  file.write_text(pre+f"\n$scenario='{scenario}'\n$log='{log}'\n"+body,encoding='utf-8-sig')
  result=subprocess.run([ps,'-NoProfile','-ExecutionPolicy','Bypass','-File',str(file),r'C:\installed'],capture_output=True,text=True)
  actual=','.join(dict.fromkeys(log.read_text().split())) if log.exists() else ''
  assert result.returncode==expected and actual==stopped,(scenario,result.returncode,actual,result.stderr)
  print(f'{scenario}: PASS')
