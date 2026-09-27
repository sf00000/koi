@echo off
rem ============================================================
rem  Koi one-click build script
rem  Uses the .NET Framework compiler built into Windows.
rem  No Visual Studio or SDK required.
rem  Usage: double-click this file, or run in cmd.
rem ============================================================
setlocal

set "CSCDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%CSCDIR%\csc.exe" set "CSCDIR=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%CSCDIR%\csc.exe" (
    echo [ERROR] .NET Framework compiler not found. Install .NET Framework 4.x first.
    pause
    exit /b 1
)

rem csc resolves relative -r: paths against the current directory,
rem so switch to the compiler directory where the WPF assemblies live.
cd /d "%CSCDIR%"

csc -nologo -target:winexe -out:"%~dp0Koi.exe" ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:Microsoft.VisualBasic.dll ^
  -r:System.Windows.Forms.dll ^
  -r:WPF\PresentationFramework.dll -r:WPF\PresentationCore.dll -r:WPF\WindowsBase.dll ^
  -r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll" ^
  "%~dp0Program.cs"

if errorlevel 1 (
    echo.
    echo [FAILED] Build errors above.
    pause
    exit /b 1
)

echo.
echo [OK] Build succeeded: %~dp0Koi.exe
pause
