@echo off
setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Kunde inte hitta csc.exe i .NET Framework 4.x.
    exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
    /out:"%~dp0ComfyUI Model Manager.exe" ^
    /win32icon:"%~dp0src\app.ico" ^
    /win32manifest:"%~dp0src\app.manifest" ^
    /resource:"%~dp0src\app.ico",ComfyUI.ModelManager.AppIcon.ico ^
    /resource:"%~dp0lang\_template.ini",ComfyUI.ModelManager.English.ini ^
    /resource:"%~dp0LICENSE",ComfyUI.ModelManager.License.txt ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Web.Extensions.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll ^
    "%~dp0src\Program.cs" ^
    "%~dp0src\AssemblyInfo.cs" ^
    "%~dp0src\IniFile.cs" ^
    "%~dp0src\Lang.cs" ^
    "%~dp0src\LanguageDialog.cs" ^
    "%~dp0src\WorkflowFilter.cs" ^
    "%~dp0src\Scanner.cs" ^
    "%~dp0src\MainForm.cs" ^
    "%~dp0src\SelfTest.cs"

if errorlevel 1 (
    echo.
    echo BYGGET MISSLYCKADES.
    exit /b 1
)

copy /y "%~dp0src\app.config" "%~dp0ComfyUI Model Manager.exe.config" >nul

echo.
echo Klar: "%~dp0ComfyUI Model Manager.exe"
exit /b 0
