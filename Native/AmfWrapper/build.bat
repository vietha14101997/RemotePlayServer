@echo off
setlocal

echo ======================================
echo AMF Wrapper Build Script
echo ======================================

set SCRIPT_DIR=%~dp0
set AMF_DIR=%SCRIPT_DIR%amf
set BUILD_DIR=%SCRIPT_DIR%build

:: Check if AMF SDK exists
if not exist "%AMF_DIR%\public\include\core\Factory.h" (
    echo.
    echo AMF SDK not found. Downloading from GitHub...
    echo.
    
    :: Create amf directory
    if not exist "%AMF_DIR%" mkdir "%AMF_DIR%"
    
    :: Download AMF SDK using git
    where git >nul 2>&1
    if errorlevel 1 (
        echo ERROR: Git not found. Please install Git or download AMF SDK manually.
        echo Download from: https://github.com/GPUOpen-LibrariesAndSDKs/AMF
        echo Extract to: %AMF_DIR%
        pause
        exit /b 1
    )
    
    echo Cloning AMF SDK (sparse checkout for headers only)...
    cd "%SCRIPT_DIR%"
    
    :: Clone only the public headers (minimal download)
    git clone --depth 1 --filter=blob:none --sparse https://github.com/GPUOpen-LibrariesAndSDKs/AMF.git amf_temp
    cd amf_temp
    git sparse-checkout set amf/public
    
    :: Move files
    xcopy /E /Y amf\public "%AMF_DIR%\public\"
    cd ..
    rmdir /S /Q amf_temp
    
    echo AMF SDK downloaded successfully.
)

:: Check for Visual Studio
set VS_PATH=
for /f "tokens=*" %%i in ('"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath 2^>nul') do set VS_PATH=%%i

if "%VS_PATH%"=="" (
    echo ERROR: Visual Studio not found. Please install Visual Studio 2019 or later.
    pause
    exit /b 1
)

echo Found Visual Studio at: %VS_PATH%

:: Setup Visual Studio environment
call "%VS_PATH%\VC\Auxiliary\Build\vcvars64.bat"

:: Check for CMake
where cmake >nul 2>&1
if errorlevel 1 (
    echo ERROR: CMake not found. Please install CMake.
    pause
    exit /b 1
)

:: Create build directory
if not exist "%BUILD_DIR%" mkdir "%BUILD_DIR%"
cd "%BUILD_DIR%"

:: Configure with CMake
echo.
echo Configuring with CMake...
cmake -G "Visual Studio 17 2022" -A x64 ..

if errorlevel 1 (
    echo CMake configuration failed.
    pause
    exit /b 1
)

:: Build Release
echo.
echo Building Release configuration...
cmake --build . --config Release

if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

:: Copy to C# project bin
echo.
echo Copying DLL to project output...
set OUTPUT_DIR=%SCRIPT_DIR%..\..\bin\Release\net9.0-windows10.0.26100.0
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"
copy /Y "%BUILD_DIR%\Release\AmfWrapper.dll" "%OUTPUT_DIR%\"

echo.
echo ======================================
echo Build completed successfully!
echo DLL: %OUTPUT_DIR%\AmfWrapper.dll
echo ======================================

pause
