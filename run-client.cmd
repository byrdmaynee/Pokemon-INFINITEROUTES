@echo off
rem Builds and launches the Infinite Routes client. Needs the GODOT variable pointing at the Godot 4.7 .NET exe.
if "%GODOT%"=="" (
  echo Set GODOT to your Godot 4.7 .NET executable first, e.g.
  echo   setx GODOT "C:\Godot\Godot_v4.7.2-stable_mono_win64.exe"
  exit /b 1
)
dotnet build "%~dp0src\InfiniteRoutes.Client" -v quiet -nologo || exit /b 1
start "" "%GODOT%" --path "%~dp0src\InfiniteRoutes.Client" %*
