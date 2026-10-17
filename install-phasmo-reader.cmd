@echo off
rem Installs the OUTSIDE Phasmophobia reader + its own overlay copy into D:\Tools\phasmo-reader.
rem Nothing goes into the game folder. The overlay is a copy of the Other Side Reader overlay (same look),
rem pointed at this reader's feed and at the Phasmophobia window.
set "R=D:\Tools\phasmo-reader"
set "SRC=%~dp0"
set "OV=D:\Tools\tos-reader\overlay"

echo Building the reader...
dotnet build "%SRC%PhasmoReader.csproj" -c Release -o "%R%\bin" -v q -nologo || (echo Build FAILED & exit /b 1)

echo Copying the overlay (a separate copy, the Other Side one stays as it is)...
robocopy "%OV%" "%R%\overlay" /e /xf log.txt /njh /njs /nfl /ndl >nul
node "%SRC%phasmo-config.js" "%R%\overlay\config.json" "D:/Tools/phasmo-reader/soundfeed.txt" || (echo Config edit FAILED & exit /b 1)
findstr /c:"phasmo-reader/soundfeed.txt" "%R%\overlay\config.json" >nul || (echo Could not point the overlay at the reader feed & exit /b 1)

copy /y "%SRC%start-phasmo-reader.cmd" "%R%\" >nul
powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\Phasmo Reader.lnk'); $s.TargetPath='%R%\start-phasmo-reader.cmd'; $s.WorkingDirectory='%R%'; $s.IconLocation='%R%\overlay\OtherSideSound.exe,0'; $s.WindowStyle=7; $s.Save()"

if exist "%R%\bin\PhasmoReader.exe" (echo Installed. Desktop shortcut: "Phasmo Reader") else (echo Install FAILED)
