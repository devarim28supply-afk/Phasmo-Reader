@echo off
rem Phasmo Reader: shows Phasmophobia's sounds on screen. Reads the game from OUTSIDE, read-only.
rem Start the game first or after, either way works. F8 = hide/show overlay, Ctrl+F8 = quit overlay.
start "Phasmo Reader" /min /D "%~dp0reader" "%~dp0reader\PhasmoReader.exe"
start "" /D "%~dp0overlay" "%~dp0overlay\OtherSideSound.exe"
