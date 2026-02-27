@echo off
setlocal

echo ======================================
echo  Native Encoder Wrappers - Build All
echo ======================================
echo.

:: Find MSBuild - check PATH first (Developer Command Prompt), then vswhere
set "MSB="
where MSBuild.exe >nul 2>nul
if %errorlevel%==0 (
    for /f "tokens=*" %%i in ('where MSBuild.exe') do (
        if not defined MSB set "MSB=%%i"
    )
)

:: Fallback: Find via vswhere
if not defined MSB (
    for /f "tokens=*" %%i in ('"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2^>nul') do set "MSB=%%i"
)

if not defined MSB (
    echo ERROR: MSBuild not found. Please run from Developer Command Prompt or install Visual Studio 2019+.
    pause
    exit /b 1
)

echo Found MSBuild: %MSB%
echo.

set "ROOT=%~dp0"
set FAILED=0

echo --- [1/3] NvencWrapper ---
"%MSB%" "%ROOT%NvencWrapper\NvencWrapper.vcxproj" /p:Configuration=Release /p:Platform=x64 /v:minimal
if errorlevel 1 (
    echo [FAILED] NvencWrapper
    set FAILED=1
) else (
    echo [OK] NvencWrapper
)
echo.

echo --- [2/3] AmfWrapper ---
"%MSB%" "%ROOT%AmfWrapper\AmfWrapper.vcxproj" /p:Configuration=Release /p:Platform=x64 /v:minimal
if errorlevel 1 (
    echo [FAILED] AmfWrapper
    set FAILED=1
) else (
    echo [OK] AmfWrapper
)
echo.

echo --- [3/3] QsvWrapper ---
"%MSB%" "%ROOT%QsvWrapper\QsvWrapper.vcxproj" /p:Configuration=Release /p:Platform=x64 /v:minimal
if errorlevel 1 (
    echo [FAILED] QsvWrapper
    set FAILED=1
) else (
    echo [OK] QsvWrapper
)
echo.

echo ======================================
if %FAILED%==1 (
    echo  BUILD FAILED - Check errors above
) else (
    echo  ALL 3 WRAPPERS BUILT SUCCESSFULLY
)
echo ======================================

pause
