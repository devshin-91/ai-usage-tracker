@echo off
rem Builds dist\AIUsageTracker.exe with the C# compiler bundled in Windows (.NET Framework 4.8). No downloads needed.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4.x csc.exe not found.
  exit /b 1
)
if not exist dist mkdir dist

set FLAGS=/nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu /win32manifest:src\app.manifest /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll

if not exist src\app.ico (
  "%CSC%" %FLAGS% /out:dist\AIUsageTracker.exe src\*.cs || exit /b 1
  dist\AIUsageTracker.exe --write-icon src\app.ico
)
"%CSC%" %FLAGS% /win32icon:src\app.ico /out:dist\AIUsageTracker.exe src\*.cs || exit /b 1
echo Built dist\AIUsageTracker.exe
