#!/bin/sh
# Builds the Mac host + engine on Linux (no Windows Desktop SDK exists there) and runs the portability scan.
# Run from the repo root on any machine with Docker:
#   docker run --rm -v "$PWD:/repo:ro" -v "<folder with Rhino.Runtime.Code.dll>:/rhino:ro" \
#     mcr.microsoft.com/dotnet/sdk:8.0 sh /repo/mac/tests/linux_build.sh
set -e
cp -r /repo /work && cd /work
rm -rf mac/out mac/*/obj mac/tools/*/obj
dotnet --version
for p in Nextra.Host.Mac Nextra.Engine.Mac; do
  dotnet build mac/$p/$p.csproj -c Release -nologo -v q -p:RhinoCodeDll=/rhino/Rhino.Runtime.Code.dll
done
ls -l mac/out/Release
dotnet run --project mac/tools/PortScan -v q -- check mac/out/Release/Nextra.Host.rhp mac/out/Release/Nextra.Engine.dll
