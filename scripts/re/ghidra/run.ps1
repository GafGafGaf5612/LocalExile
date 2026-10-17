# usage: run.ps1 <Script.java> <outfile> <args...>
param([string]$Script, [Parameter(ValueFromRemainingArguments=$true)][string[]]$ScriptArgs)
$r = "C:\Users\Frost\Desktop\Projects\Game"
$env:JAVA_HOME = "$r\Tools\jdk-21"
& "$r\Tools\ghidra_12.1.4_PUBLIC_20260921\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "$r\Tools\ghidra-projects" poe3293 -process PathOfExile.exe -noanalysis -readOnly -scriptPath "$r\scripts\re\ghidra" -postScript $Script @ScriptArgs 2>&1 | Select-String -Pattern "ERROR|Exception" | Select-Object -First 10
