@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found
    exit /b 1
)
if not exist dnlib.dll (
    echo dnlib.dll not found in current directory. Download the net45 build from https://www.nuget.org/packages/dnlib and place it here.
    exit /b 1
)
"%CSC%" /nologo /target:exe /out:patch.exe /r:dnlib.dll src\patch.cs || exit /b 1
echo Build OK: patch.exe
