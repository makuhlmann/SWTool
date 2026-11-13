@echo off
rem Builds SWTool with the Visual Studio 2015 build tools (MSBuild 14).
setlocal
set "MSBUILD=%ProgramFiles(x86)%\MSBuild\14.0\Bin\MSBuild.exe"
if not exist "%MSBUILD%" (
  echo MSBuild 14.0 was not found at "%MSBUILD%".
  exit /b 1
)
"%MSBUILD%" "%~dp0SWTool.sln" /nologo /v:minimal /p:Configuration=Release %*
if errorlevel 1 exit /b 1
echo.
echo Built: %~dp0SWTool\bin\Release\SWTool.exe
