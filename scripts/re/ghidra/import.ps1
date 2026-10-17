# Creates the Ghidra project the RE scripts use: Tools/ghidra-projects/poe3293, program PathOfExile.exe, imported from
# Client/Game.exe and fully analyzed. One-time; takes a few hours (log in Tools/ghidra-projects/analyze.log).
# Needs Ghidra 12.1.4 in Tools/ghidra_12.1.4_PUBLIC_20260921 and JDK 21 in Tools/jdk-21 (Agents/GettingStarted.md).
$r = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$env:JAVA_HOME = "$r\Tools\jdk-21"
$projects = "$r\Tools\ghidra-projects"
New-Item -ItemType Directory -Force $projects | Out-Null
# Import under the official file name: the scripts open the program as PathOfExile.exe.
$exe = "$projects\PathOfExile.exe"
Copy-Item "$r\Client\Game.exe" $exe
& "$r\Tools\ghidra_12.1.4_PUBLIC_20260921\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat" $projects poe3293 `
    -import $exe -overwrite -analysisTimeoutPerFile 28800 -max-cpu 8 *> "$projects\analyze.log"
Remove-Item $exe
