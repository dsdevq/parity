@echo off
rem Windows wrapper: runs the debug build of parity via dotnet, building it first if needed.
setlocal
set HERE=%~dp0..
if exist "%HERE%\dist\win-x64\parity.exe" ( "%HERE%\dist\win-x64\parity.exe" %* & exit /b %ERRORLEVEL% )
if not exist "%HERE%\src\Parity\bin\Debug\net10.0\parity.dll" dotnet build "%HERE%\src\Parity" -nologo -v q >nul
dotnet "%HERE%\src\Parity\bin\Debug\net10.0\parity.dll" %*
