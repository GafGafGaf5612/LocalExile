#!/bin/bash
# usage: g.sh <Script.java> [args...]  — runs a script against the saved poe3293 project (read-only).
# Paths are relative to the repository this script is in.
# GLINES=<n> shows more than the first 8 matching log lines.
R=$(cd "$(dirname "$0")/../../.." && pwd); W=$(cd "$R" && pwd -W)
S=$(cd "$(dirname "$0")" && pwd -W | tr / '\\')
export JAVA_HOME="$W\Tools\jdk-21"
cd "$R" && cmd //c "Tools\ghidra_12.1.4_PUBLIC_20260921\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat Tools\ghidra-projects poe3293 -process PathOfExile.exe -noanalysis -readOnly -scriptPath $S -postScript $*" 2>&1 | grep -E "ERROR|Exception|SCRIPT" | head -${GLINES:-8}
