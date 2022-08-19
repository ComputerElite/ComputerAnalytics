@echo off

set /p changelog=Changelog: 
echo Pushing to GitHub

git add *
git commit -m "%changelog%"
for /f %%i in ('git rev-parse HEAD') do set id=%%i
git push origin main

echo Pushed to GitHub

echo Commit id %id%

cd bin\Debug\net6.0\
echo Deleting existing update zip
del net6.0.zip

echo Creating new update zip
7z a net6.0.zip *.dll *.pdb *.exe *.json frontend ref runtimes

echo Created update zip
echo Changelog:
echo.
echo %changelog%\nFull changes: https://github.com/ComputerElite/ComputerAnalytics/commit/%id%

start "" https://analytics.rui2015.me/
cd..
cd..
cd..