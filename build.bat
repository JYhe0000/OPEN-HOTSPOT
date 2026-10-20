@echo off
setlocal

set FW35=%WINDIR%\Microsoft.NET\Framework\v3.5
set FW20=%WINDIR%\Microsoft.NET\Framework\v2.0.50727
set CSC=%FW35%\csc.exe

if not exist "%CSC%" (
  echo .NET Framework 3.5 compiler not found.
  pause
  exit /b 1
)

pushd "%~dp0"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /codepage:65001 ^
 /win32icon:app.ico /win32manifest:app.manifest /out:OpenHotspot.exe ^
 /r:"%FW20%\System.dll" ^
 /r:"%FW20%\System.Drawing.dll" ^
 /r:"%FW20%\System.Management.dll" ^
 /r:"%FW20%\System.Security.dll" ^
 /r:"%FW20%\System.ServiceProcess.dll" ^
 /r:"%FW20%\System.Windows.Forms.dll" ^
 /r:"%FW20%\System.Xml.dll" ^
 Program.cs AppConfig.cs MainForm.cs Properties\AssemblyInfo.cs ^
 Core\HotspotService.cs Core\IcsManager.cs Core\NetshRunner.cs Core\NetworkInventory.cs

set RESULT=%ERRORLEVEL%
popd

if not "%RESULT%"=="0" (
  echo Build failed.
  pause
  exit /b 1
)

echo Build succeeded: OpenHotspot.exe
pause
