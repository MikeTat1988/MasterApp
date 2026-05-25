# Local `gemma4:e2b` Through Ollama On Windows

This setup is for running `gemma4:e2b` locally on a laptop and for future MasterApp integration without keeping the model loaded in memory all the time.

## Where Ollama Is Installed

- Program folder: `%LOCALAPPDATA%\Programs\Ollama`
- Main executable: `%LOCALAPPDATA%\Programs\Ollama\ollama.exe`
- Local service files and logs: `%LOCALAPPDATA%\Ollama`

Confirmed path on this machine:

- `C:\Users\micha\AppData\Local\Programs\Ollama\ollama.exe`

## Where The Model Is Stored

- Default model store: `%USERPROFILE%\.ollama\models`
- `gemma4:e2b` model manifest: `%USERPROFILE%\.ollama\models\manifests\registry.ollama.ai\library\gemma4\e2b`

Confirmed path on this machine:

- `C:\Users\micha\.ollama\models`

## Basic Ollama Commands

```bat
ollama pull gemma4:e2b
ollama run gemma4:e2b
ollama stop gemma4:e2b
```

## Start And Warm Up The Model

From the MasterApp repository root:

```bat
scripts\start_gemma.bat
```

What the script does:

- checks that `ollama.exe` is installed
- checks that the `gemma4:e2b` model is already downloaded
- sends a short warm-up request

After that, the model should respond faster to the next requests.

## Stop And Unload The Model

```bat
scripts\stop_gemma.bat
```

The script runs:

```bat
ollama stop gemma4:e2b
```

Note:

- after `stop`, the model can briefly appear in `ollama ps` as `Stopping...`
- that is normal; it usually disappears from the list after a few seconds

## Test Through The CLI

```bat
scripts\test_gemma.bat
```

The script sends an English prompt and prints the model response.

## Test Through The API

Normal request with standard Ollama behavior:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test_gemma_api.ps1
```

Request that unloads the model immediately after the response:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test_gemma_api.ps1 -ImmediateUnload
```

The second mode sends this API shape:

```json
{
  "model": "gemma4:e2b",
  "prompt": "Reply briefly in English: the local API test succeeded.",
  "stream": false,
  "keep_alive": 0
}
```

Practical note:

- `keep_alive=0` asks Ollama to unload the model immediately after the response
- actual memory cleanup can still take a few seconds

Local Ollama API URL:

- `http://localhost:11434`

## Should The Model Stay Loaded All The Time?

No. That is not recommended for this use case.

The best mode here is:

- rely on Ollama's default idle behavior by default
- warm up the model manually only when needed
- unload it explicitly with `ollama stop gemma4:e2b` when needed
- use `keep_alive=0` for one-off API calls

## How This Fits MasterApp

Recommended pattern for future integration:

- MasterApp sends normal requests to the local model through the Ollama API
- the local model helps with intent, structured answers, and simple local tasks
- any real computer actions must remain behind MasterApp's approval layer

`gemma4:e2b` is a good fit as a local, private, and relatively cheap model for:

- fast local requests
- draft instructions
- short summaries
- preparing action proposals for the computer

It should not be the only agent for complex coding tasks or for executing risky actions without confirmation.
