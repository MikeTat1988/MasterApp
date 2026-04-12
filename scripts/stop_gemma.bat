@echo off
setlocal

set MODEL=gemma4:e2b
set OLLAMA_EXE=%LOCALAPPDATA%\Programs\Ollama\ollama.exe

if not exist "%OLLAMA_EXE%" (
  echo [stop_gemma] Ollama not found: "%OLLAMA_EXE%"
  exit /b 1
)

echo [stop_gemma] Unloading %MODEL% from memory...
"%OLLAMA_EXE%" stop %MODEL%
exit /b %ERRORLEVEL%
