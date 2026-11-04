#!/bin/bash
# usage: run-early.sh <label> <iterations> <extra-connstring> [ENV=val ...]
cd /g/projects-ext/SqlClient-scratch/ResetReproLocal
label=$1; n=$2; extra=$3; shift 3
echo "##### $label"
env SQLCLIENT_REPRO_EARLY_READ=1 "$@" timeout 90 ./bin/Release/net10.0/ResetReproLocal.exe $n $extra > trace.txt 2>&1
code=$?
[ $code -ne 0 ] && echo "exit=$code (124=timeout)"
grep -E "^ *[0-9]+ x|side-effect|bench:|Unhandled" trace.txt | sed -E 's/State=Closed //; s/IsTransient=False //' | cut -c1-150
if [ -n "$SHOWTRACE" ]; then m=$(grep -n "reset bit=True) bytes=126" trace.txt | head -1 | cut -d: -f1); [ -n "$m" ] && sed -n "$((m-2)),$((m+8))p" trace.txt | cut -c1-150; fi
taskkill //IM ResetReproLocal.exe //F >/dev/null 2>&1; true
