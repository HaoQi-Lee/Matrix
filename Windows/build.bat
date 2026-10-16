@echo off
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [ERROR] .NET Framework csc.exe not found.
    exit /b 1
)
"%CSC%" /nologo /target:winexe /out:Matrix.scr /r:System.Windows.Forms.dll /r:System.Drawing.dll src\Program.cs
if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)
echo [OK] Built Matrix.scr
