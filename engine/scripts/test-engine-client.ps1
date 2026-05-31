# Mock-Engine-Client für die WPF-Pipe — zum Test ohne iRacing/VR.
# Connectet zur WPF-GUI, sendet hello, druckt empfangene Nachrichten,
# sendet auf Tastendruck einen Test-Status.
#
# Usage: pwsh -File test-engine-client.ps1
# Beenden: Ctrl+C

$ErrorActionPreference = 'Stop'

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
    '.', 'VROverlayLayout',
    [System.IO.Pipes.PipeDirection]::InOut,
    [System.IO.Pipes.PipeOptions]::Asynchronous)

Write-Host 'Connecting to \\.\pipe\VROverlayLayout ...' -ForegroundColor Cyan
try {
    $pipe.Connect(5000)
} catch {
    Write-Host 'Connect failed — WPF-GUI läuft nicht?' -ForegroundColor Red
    exit 1
}
Write-Host 'Connected.' -ForegroundColor Green

function Send-Json($obj) {
    $json = ($obj | ConvertTo-Json -Compress -Depth 10)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $len = [BitConverter]::GetBytes([uint32]$bytes.Length)
    $pipe.Write($len, 0, 4)
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
    Write-Host "TX: $json" -ForegroundColor Yellow
}

# Hello senden
Send-Json @{ type = 'hello'; app = 'iRacing'; engineVersion = 'mock-0.1' }

# Status-Sender im Hintergrund (alle 5s)
$statusJob = Start-Job -ScriptBlock {
    param($pipeName)
    Start-Sleep 5
    # in this job we can't share the pipe, only the main thread sends.
}

# Read-Loop
$lenBuf = New-Object byte[] 4
try {
    while ($pipe.IsConnected) {
        $n = $pipe.Read($lenBuf, 0, 4)
        if ($n -eq 0) { Write-Host 'Server closed pipe.' -ForegroundColor Yellow; break }
        if ($n -ne 4) { Write-Host "short length read: $n" -ForegroundColor Red; break }
        $len = [BitConverter]::ToUInt32($lenBuf, 0)
        if ($len -eq 0 -or $len -gt 16MB) { Write-Host "bad length: $len" -ForegroundColor Red; break }
        $body = New-Object byte[] $len
        $read = 0
        while ($read -lt $len) {
            $got = $pipe.Read($body, $read, $len - $read)
            if ($got -eq 0) { break }
            $read += $got
        }
        if ($read -ne $len) { break }
        $msg = [System.Text.Encoding]::UTF8.GetString($body)
        Write-Host "RX: $msg" -ForegroundColor Green
    }
} finally {
    Stop-Job $statusJob -ErrorAction SilentlyContinue
    Remove-Job $statusJob -ErrorAction SilentlyContinue
    $pipe.Dispose()
    Write-Host 'Disconnected.' -ForegroundColor Cyan
}
