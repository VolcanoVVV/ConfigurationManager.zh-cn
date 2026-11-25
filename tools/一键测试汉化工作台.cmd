@echo off
chcp 65001 >nul
python -c "import sys; raise SystemExit(sys.version_info < (3, 10))" >nul 2>&1
if not errorlevel 1 (
    python -X utf8 "%~dp0workbench_selftest.py" --open-report %*
    goto done
)
py -3 -c "import sys; raise SystemExit(sys.version_info < (3, 10))" >nul 2>&1
if errorlevel 1 (
    echo Python 3.10 or later is required.
    goto done
)
py -3 -X utf8 "%~dp0workbench_selftest.py" --open-report %*
:done
pause
