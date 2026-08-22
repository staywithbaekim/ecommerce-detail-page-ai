param(
    [int]$Port = 17842,
    [int]$MaxHandshakes = 1
)

$ErrorActionPreference = 'Stop'
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
Write-Output "UXP validation bridge listening on http://127.0.0.1:$Port"

$accepted = 0
try {
    while ($accepted -lt $MaxHandshakes) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $false, 1024, $true)
            $requestLine = $reader.ReadLine()
            $headers = @{}
            while ($true) {
                $line = $reader.ReadLine()
                if ([string]::IsNullOrEmpty($line)) { break }
                $separator = $line.IndexOf(':')
                if ($separator -gt 0) {
                    $headers[$line.Substring(0, $separator).Trim().ToLowerInvariant()] = $line.Substring($separator + 1).Trim()
                }
            }

            $parts = $requestLine -split ' '
            $method = $parts[0]
            $path = $parts[1]
            $contentLength = if ($headers.ContainsKey('content-length')) { [int]$headers['content-length'] } else { 0 }
            $buffer = New-Object char[] $contentLength
            $read = 0
            while ($read -lt $contentLength) {
                $count = $reader.Read($buffer, $read, $contentLength - $read)
                if ($count -le 0) { break }
                $read += $count
            }
            $body = if ($read -gt 0) { -join $buffer[0..($read - 1)] } else { '' }

            $status = '200 OK'
            if ($method -eq 'OPTIONS') {
                $response = @{ ok = $true }
            } elseif ($method -ne 'POST' -or $path -ne '/handshake') {
                $status = '404 Not Found'
                $response = @{ ok = $false; error = @{ code = 'NOT_FOUND'; message = 'Only POST /handshake is supported.' } }
            } else {
                try {
                    $request = $body | ConvertFrom-Json
                    if ($request.protocol -ne 'render-plan/v1') { throw 'Unsupported protocol.' }
                    if ($request.plugin_id -ne 'local.ecommerce.uxp-validation') { throw 'Unexpected plugin id.' }
                    $response = @{
                        ok = $true
                        protocol = 'render-plan/v1'
                        session_id = [guid]::NewGuid().ToString()
                        accepted_at = [DateTime]::UtcNow.ToString('o')
                        bridge = 'powershell-loopback-validation/0.1.0'
                    }
                    $accepted += 1
                } catch {
                    $status = '400 Bad Request'
                    $response = @{ ok = $false; error = @{ code = 'INVALID_HANDSHAKE'; message = $_.Exception.Message } }
                }
            }

            $json = $response | ConvertTo-Json -Depth 6 -Compress
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
            $header = "HTTP/1.1 $status`r`nContent-Type: application/json; charset=utf-8`r`nContent-Length: $($bytes.Length)`r`nAccess-Control-Allow-Origin: *`r`nAccess-Control-Allow-Headers: Content-Type`r`nConnection: close`r`n`r`n"
            $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
            $stream.Write($headerBytes, 0, $headerBytes.Length)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush()
            Write-Output "$method $path -> $status"
        } finally {
            $client.Dispose()
        }
    }
} finally {
    $listener.Stop()
}

Write-Output "UXP validation bridge stopped after $accepted accepted handshake(s)."
