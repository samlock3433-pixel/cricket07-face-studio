@echo off
setlocal
if exist "%~dp0..\tools\dotnet-sdk\dotnet.exe" set "DOTNET_ROOT=%~dp0..\tools\dotnet-sdk"
if not exist "%~dp0dist\Cricket07.FaceStudio.exe" (
  echo Face Studio has not been published. See README.md for the build command.
  pause
  exit /b 1
)
start "" /D "%~dp0dist" "%~dp0dist\Cricket07.FaceStudio.exe"
