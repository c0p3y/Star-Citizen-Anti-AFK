@echo off
rem Builds StarCitizenAntiAFK.exe with the C# compiler that ships with Windows (no SDK / Visual Studio needed).
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo Could not find csc.exe - is .NET Framework 4.x installed? & pause & exit /b 1)
"%CSC%" /nologo /target:winexe /optimize+ /out:StarCitizenAntiAFK.exe /win32manifest:src\app.manifest /win32icon:src\app.ico /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\StarCitizenAntiAFK.cs
if errorlevel 1 (echo Build failed) else (echo Built StarCitizenAntiAFK.exe)
pause
