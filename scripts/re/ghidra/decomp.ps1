# usage: decomp.ps1 <outfile> <addr[:depth]>...
param([string]$Out, [Parameter(ValueFromRemainingArguments=$true)][string[]]$Addrs)
$r = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$env:JAVA_HOME = "$r\Tools\jdk-21"
& "$r\Tools\ghidra_12.1.4_PUBLIC_20260921\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" "$r\Tools\ghidra-projects" poe3293 -process PathOfExile.exe -noanalysis -readOnly -scriptPath "$r\scripts\re\ghidra" -postScript Decomp.java $Out @Addrs 2>&1 | Select-String -Pattern "ERROR|Exception|REPORT" | Select-Object -First 10
