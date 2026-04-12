param(
    [switch]$ImmediateUnload
)

$ErrorActionPreference = 'Stop'

$model = 'gemma4:e2b'
$uri = 'http://localhost:11434/api/generate'

$body = @{
    model = $model
    prompt = 'Reply in Russian: local API test passed.'
    stream = $false
}

if ($ImmediateUnload) {
    $body.keep_alive = 0
}

$response = Invoke-RestMethod -Uri $uri -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 5)

Write-Host ''
if ($ImmediateUnload) {
    Write-Host '[test_gemma_api] Mode: keep_alive=0'
} else {
    Write-Host '[test_gemma_api] Mode: default Ollama keep-alive'
}

Write-Host "[test_gemma_api] Model: $model"
Write-Host ''
Write-Output $response.response
