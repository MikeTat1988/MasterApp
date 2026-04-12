@echo off
setlocal

set MODEL=gemma4:e2b
set OLLAMA_EXE=%LOCALAPPDATA%\Programs\Ollama\ollama.exe

if not exist "%OLLAMA_EXE%" (
  echo [test_gemma] Ollama not found: "%OLLAMA_EXE%"
  exit /b 1
)

echo [test_gemma] Testing %MODEL%...
echo.
"%OLLAMA_EXE%" run %MODEL% "Reply in Russian: local test passed."
exit /b %ERRORLEVEL%
