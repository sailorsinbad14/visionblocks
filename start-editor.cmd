@echo off
rem Start VisionBlocks built from source. Build first: dotnet build src\VisionBlocks.Studio -c Release
rem Started from Explorer, the editor is in the foreground, so its Run button can hand the foreground to the target window.
start "" "%~dp0src\VisionBlocks.Studio\bin\Release\net10.0-windows10.0.22621.0\VisionBlocksStudio.exe"
