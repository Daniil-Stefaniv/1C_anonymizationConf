@echo off
setlocal
pushd "%~dp0"
if "%~1"=="" (
  set /p TARGET=Введите путь к папке выгрузки 1С: 
) else (
  set "TARGET=%~1"
)
"%~dp0python\python.exe" "%~dp0clean_1c_comments.py" "%TARGET%" --jobs 8 --validate-dry-run
pause
popd
