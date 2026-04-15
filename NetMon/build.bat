@echo off
setlocal

echo ============================================================
echo  NetMon - build script
echo ============================================================

REM --- framework-dependent (small, requires .NET 8 runtime) ---
echo.
echo [1/2]  Building framework-dependent release...
dotnet publish NetMon.csproj -c Release -r win-x64 --self-contained false ^
    -o bin\publish\framework-dependent
if errorlevel 1 goto :err

REM --- self-contained single-file (no runtime install needed) ---
echo.
echo [2/2]  Building self-contained single-file...
dotnet publish NetMon.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o bin\publish\self-contained
if errorlevel 1 goto :err

echo.
echo ============================================================
echo  Done!
echo  Framework-dependent : bin\publish\framework-dependent\NetMon.exe
echo  Self-contained       : bin\publish\self-contained\NetMon.exe
echo ============================================================
goto :eof

:err
echo.
echo BUILD FAILED (exit code %errorlevel%)
exit /b 1
