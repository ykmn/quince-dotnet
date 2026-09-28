param(
    [uri]$Endpoint = 'http://127.0.0.1:5000/api/monitoring/zabbix'
)

# Windows PowerShell 2.0 (.NET 2.0/3.5), 5.1 and PowerShell 7 (Core). One JSON response, no profile/banner/progress.
# PowerShell 2.0 has neither System.Net.Http nor ConvertFrom-Json, so the request goes through HttpWebRequest
# and, where ConvertFrom-Json is missing, the JSON is validated with JavaScriptSerializer (.NET 3.5).
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$response = $null
$stream = $null
$reader = $null
try {
    if (-not $Endpoint.IsLoopback -or ($Endpoint.Scheme -ne 'http' -and $Endpoint.Scheme -ne 'https')) {
        throw 'Endpoint must be a loopback HTTP(S) URL.'
    }
    if ($Endpoint.Scheme -eq 'https') {
        # Best effort: TLS 1.2 (SecurityProtocolType.Tls12 = 3072) is absent from the enum on old .NET.
        try { [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072 } catch { }
    }

    $request = [System.Net.HttpWebRequest][System.Net.WebRequest]::Create($Endpoint)
    $request.Method = 'GET'
    $request.Proxy = $null
    $request.AllowAutoRedirect = $false
    $request.Timeout = 5000
    $request.ReadWriteTimeout = 5000
    try {
        $response = $request.GetResponse()
    }
    catch [System.Net.WebException] {
        $failed = $_.Exception.Response
        if ($null -ne $failed) {
            $status = [int]$failed.StatusCode
            $failed.Close()
            throw ('HTTP ' + $status + ' from ' + $Endpoint.AbsoluteUri)
        }
        throw
    }
    $code = [int]$response.StatusCode
    if ($code -lt 200 -or $code -gt 299) { throw ('HTTP ' + $code + ' from ' + $Endpoint.AbsoluteUri) }

    $stream = $response.GetResponseStream()
    $reader = New-Object System.IO.StreamReader($stream, (New-Object System.Text.UTF8Encoding($false)))
    $body = $reader.ReadToEnd()

    if (Get-Command ConvertFrom-Json -ErrorAction SilentlyContinue) {
        $snapshot = $body | ConvertFrom-Json
        $valid = $snapshot.schema_version -eq 1 -and $null -ne $snapshot.channels -and $null -ne $snapshot.generated_at
    }
    else {
        Add-Type -AssemblyName System.Web.Extensions
        $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $serializer.MaxJsonLength = [int]::MaxValue
        $snapshot = $serializer.DeserializeObject($body)
        $valid = $snapshot -is [System.Collections.IDictionary] -and $snapshot['schema_version'] -eq 1 `
            -and $null -ne $snapshot['channels'] -and $null -ne $snapshot['generated_at']
    }
    if (-not $valid) {
        throw 'Invalid Quince monitoring snapshot (expected schema_version=1, generated_at, channels).'
    }
    [Console]::WriteLine($body)
}
catch {
    [Console]::Error.WriteLine('Quince monitoring failed: ' + $_.Exception.Message)
    exit 1
}
finally {
    if ($null -ne $reader) { $reader.Close() }
    if ($null -ne $stream) { $stream.Close() }
    if ($null -ne $response) { $response.Close() }
}
