@echo off
echo ===================================
echo RemoteOpsTool Test Script
echo ===================================
echo Hostname: %COMPUTERNAME%
echo User:     %USERNAME%
echo Date:     %DATE% %TIME%
echo ===================================
echo.
echo --- IPConfig ---
ipconfig | findstr IPv4
echo.
echo --- System Info ---
systeminfo | findstr /i "OS Name System Type Memory"
echo.
echo --- Disk ---
wmic logicaldisk get deviceid,freespace,size
echo.
echo ===================================
echo Test Complete
echo ===================================
