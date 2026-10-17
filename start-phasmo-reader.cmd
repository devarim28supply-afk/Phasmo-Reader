@echo off
rem Starts the outside Phasmophobia reader (read-only, waits for the game; log in reader.log) and its overlay.
start "Phasmo Reader" /min /D "%~dp0bin" "%~dp0bin\PhasmoReader.exe"
start "" /D "%~dp0overlay" "%~dp0overlay\OtherSideSound.exe"
