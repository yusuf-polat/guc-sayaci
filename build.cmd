@echo off
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4.x derleyicisi bulunamadi.
  exit /b 1
)
if "%1"=="logo" (
  if not exist bin mkdir bin
  "%CSC%" /nologo /out:bin\LogoGen.exe /r:System.Drawing.dll tools\LogoGen.cs || exit /b 1
  bin\LogoGen.exe assets || exit /b 1
)
if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 /out:bin\GucSayaci.exe /win32icon:assets\logo.ico /resource:assets\logo.ico,logo.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:Microsoft.VisualBasic.dll src\GucSayaci.cs || exit /b 1
echo Tamam: bin\GucSayaci.exe