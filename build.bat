@echo off
rem ============================================================
rem  PHOSPHOR PA-1 - build & package (net48 + Costura single file)
rem  usage: build.bat          = regression + build + dist\PHOSPHOR-PA1.exe
rem         build.bat notest   = skip regression, build only
rem ============================================================
setlocal
cd /d "%~dp0"

echo [1/3] Pa1.Core regression
if /i "%~1"=="notest" goto :build
dotnet run --project Pa1.Tests -c Release
if errorlevel 1 (
    echo [X] regression failed, abort
    exit /b 1
)

:build
echo [2/3] build Pa1.App Release
dotnet build Pa1.App\Pa1.App.csproj -c Release -v q
if errorlevel 1 (
    echo [X] build failed
    exit /b 1
)

echo [3/3] package dist\
if not exist dist mkdir dist
copy /y Pa1.App\bin\Release\PHOSPHOR-PA1.exe dist\
if errorlevel 1 (
    echo [X] copy to dist failed (is dist\PHOSPHOR-PA1.exe running? close it and retry)
    exit /b 1
)

echo.
echo OK: dist\PHOSPHOR-PA1.exe  (single file, double-click to run)
echo dependency: .NET Framework 4.8 (built into Win10/11)
exit /b 0
