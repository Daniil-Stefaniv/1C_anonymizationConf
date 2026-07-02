@echo off
setlocal
pushd "%~dp0"
"%~dp0python\python.exe" "%~dp0clean_1c_comments.py" --self-test
pause
popd
