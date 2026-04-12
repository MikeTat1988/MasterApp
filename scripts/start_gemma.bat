@echo off
setlocal

set MODEL=gemma4:e2b
set OLLAMA_EXE=%LOCALAPPDATA%\Programs\Ollama\ollama.exe

if not exist "%OLLAMA_EXE%" (
  echo [start_gemma] Ollama not found: "%OLLAMA_EXE%"
  echo [start_gemma] Install Ollama or check the default path.
  exit /b 1
)

echo [start_gemma] Checking local model %MODEL%...
"%OLLAMA_EXE%" list | findstr /I /C:"%MODEL%" >nul
if errorlevel 1 (
  echo [start_gemma] Model %MODEL% is not available locally.
  echo [start_gemma] Run: ollama pull %MODEL%
  exit /b 1
)

echo [start_gemma] Warming %MODEL% with a short prompt...
"%OLLAMA_EXE%" run %MODEL% "Reply with one word: ready."
if errorlevel 1 (
  echo [start_gemma] Warm-up failed.
  exit /b 1
)

echo.
echo [start_gemma] Model is warm.
echo [start_gemma] Ollama will unload it automatically after idle time by default.
exit /b 0
