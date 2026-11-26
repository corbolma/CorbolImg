@echo off
rem Compila CorbolImg.exe usando il compilatore C# incluso in Windows (.NET Framework 4.x)
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Compilatore csc.exe non trovato.
  exit /b 1
)
for %%I in ("%CSC%") do set WPF=%%~dpIWPF
cd /d "%~dp0"
if not exist corbolimg.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
rem Traduzioni: ogni lang\*.txt (tranne _template.txt) viene incorporato nell'exe
set LANGRES=
for %%F in (lang\*.txt) do if /i not "%%~nF"=="_template" call set LANGRES=%%LANGRES%% /resource:"%%F",CorbolImg.lang.%%~nF.txt
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:CorbolImg.exe ^
  /win32icon:corbolimg.ico /resource:corbolimg.ico,CorbolImg.corbolimg.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.VisualBasic.dll ^
  /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" /r:System.Xaml.dll ^
  CorbolImg.cs Viewer.cs Converter.cs Imaging.cs Grid.cs Edits.cs Ai.cs Esrgan.cs Version.cs Lang.cs OldApp.cs CorbolLink.cs %LANGRES%
if errorlevel 1 (
  echo Compilazione fallita.
  exit /b 1
)
echo Creato CorbolImg.exe
