@echo off
setlocal
pushd "%~dp0"
if "%~1"=="" (
  set /p TARGET=Введите путь к папке выгрузки 1С: 
) else (
  set "TARGET=%~1"
)
echo.
echo Будут изменены файлы в папке:
echo %TARGET%
echo.
set /p CONFIRM=Введите YES для продолжения: 
if /I not "%CONFIRM%"=="YES" (
  echo Операция отменена.
  pause
  popd
  exit /b 1
)
"%~dp0python\python.exe" "%~dp0clean_1c_comments.py" "%TARGET%" --apply --backup-dir "%TARGET%_backup_comments" --jobs 8 --validate-dry-run
pause
popd
