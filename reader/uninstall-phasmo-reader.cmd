@echo off
rem Removes the Phasmo Reader desktop shortcut and stops it. Its folder D:\Tools\phasmo-reader stays (delete it by hand if wanted).
taskkill /im PhasmoReader.exe /f >nul 2>&1
for /f "usebackq delims=" %%d in (`powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"`) do del "%%d\Phasmo Reader.lnk" >nul 2>&1
echo Removed the "Phasmo Reader" shortcut.
