@echo off
REM Builds TraceSixRT.dll (TraceSix's hardware ray tracing: a D3D12 device beside the game's D3D11 one) with MSVC,
REM and copies it into the game's TraceSix folder. The trace shader's source lives in the Unity project (shader
REM sources never sit in a mod's folder): dxc compiles it from there into build\GISixRTTrace.h first.
setlocal

set SCRIPT_DIR=%~dp0
set VCVARS="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
set DXC="C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\dxc.exe"
set HLSL="F:\Unity Game Project\tarkov stuff\Assets\Volumetric clouds\GISixRT\GISixRTTrace.hlsl"
set DEPLOY=F:\SPT4.1\BepInEx\plugins\TraceSix\native

if not exist %VCVARS% ( echo [build] vcvars64.bat not found: adjust VCVARS. & exit /b 1 )
if not exist %DXC% ( echo [build] dxc.exe not found: adjust DXC. & exit /b 1 )

pushd "%SCRIPT_DIR%"
if not exist build mkdir build

echo [build] compiling the trace shader ...
%DXC% -nologo -T cs_6_5 -E CSMain -O3 -Fh build\GISixRTTrace.h -Vn g_GISixRTTrace %HLSL%
if errorlevel 1 ( popd & echo [build] shader FAILED & exit /b 1 )
%DXC% -nologo -T cs_6_5 -E CSAo -O3 -Fh build\GISixRTAo.h -Vn g_GISixRTAo %HLSL%
if errorlevel 1 ( popd & echo [build] AO shader FAILED & exit /b 1 )
%DXC% -nologo -T cs_6_5 -E CSRefl -O3 -Fh build\GISixRTRefl.h -Vn g_GISixRTRefl %HLSL%
if errorlevel 1 ( popd & echo [build] reflection shader FAILED & exit /b 1 )
%DXC% -nologo -T cs_6_5 -E CSSun -O3 -Fh build\GISixRTSun.h -Vn g_GISixRTSun %HLSL%
if errorlevel 1 ( popd & echo [build] sun shader FAILED & exit /b 1 )

call %VCVARS% >nul
if errorlevel 1 ( popd & echo [build] failed to init MSVC env. & exit /b 1 )

echo [build] compiling GISixRT.cpp ...
REM /MT = static CRT so the DLL needs no VC++ redist on the user's machine.
cl /nologo /LD /O2 /EHsc /MT /std:c++17 /DNDEBUG /W3 ^
   /Fo"build\\" /Fe"build\TraceSixRT.dll" ^
   GISixRT.cpp ^
   /link d3d11.lib d3d12.lib dxgi.lib dxguid.lib ^
   /IMPLIB:"build\TraceSixRT.lib"
set RESULT=%errorlevel%
popd
if %RESULT% neq 0 ( echo [build] FAILED (exit %RESULT%) & exit /b %RESULT% )

if not exist "%DEPLOY%" mkdir "%DEPLOY%"
copy /Y "%SCRIPT_DIR%build\TraceSixRT.dll" "%DEPLOY%\TraceSixRT.dll" >nul
if errorlevel 1 ( echo [build] copy to %DEPLOY% FAILED - is the game running? & exit /b 1 )
echo [build] OK -^> %DEPLOY%\TraceSixRT.dll
endlocal
