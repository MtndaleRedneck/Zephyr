if ($PSVersionTable.PSVersion.Major -lt 7) { throw "PowerShell 7 required (SkipHttpErrorCheck, HttpClient pin); got $($PSVersionTable.PSVersion)" }
Write-Output "pinned-https helper running under PowerShell $($PSVersionTable.PSVersion)"
# Secure mode is HTTPS only (no plaintext fallback). The setup step above created the Player's own identity in the machine store.
# Requests go over HTTPS to 127.0.0.1 and accept ONLY the certificate whose thumbprint is that stored identity (a pin, not "skip validation").
# Status 0 means no HTTP answer at all (connect or TLS failure) and never satisfies a check below.
$tlsSrc = @(
  'using System; using System.Net.Http; using System.Net.Http.Headers; using System.Text;',
  'public static class ZephyrPin {',
  '  public static string Thumb = "";',
  '  // Returns { status, body }; status 0 = no HTTP answer (connect or TLS failure). Body only for JSON POST when given.',
  '  public static string[] Send(string method, string url, string token, string body) {',
  '    try {',
  '      var h = new HttpClientHandler();',
  '      h.ServerCertificateCustomValidationCallback = (m, c, ch, e) => c != null && Thumb.Length > 0 && string.Equals(c.Thumbprint, Thumb, StringComparison.OrdinalIgnoreCase);',
  '      using (var cl = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(10) }) {',
  '        if (!string.IsNullOrEmpty(token)) cl.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);',
  '        var req = new HttpRequestMessage(new HttpMethod(method), url);',
  '        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");',
  '        using (var res = cl.SendAsync(req).GetAwaiter().GetResult()) {',
  '          return new[] { ((int)res.StatusCode).ToString(), res.Content.ReadAsStringAsync().GetAwaiter().GetResult() };',
  '        }',
  '      }',
  '    } catch { return new[] { "0", "" }; }',
  '  }',
  '  public static int Status(string url, string token) { return int.Parse(Send("GET", url, token, null)[0]); }',
  '}') -join "`n"
if (-not ('ZephyrPin' -as [type])) { Add-Type -TypeDefinition $tlsSrc }
function Set-ZephyrPin {
  $ids = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq 'CN=Zephyr Player' })
  if ($ids.Count -ne 1) { throw "expected exactly one Zephyr Player identity in the machine store, found $($ids.Count)" }
  [ZephyrPin]::Thumb = $ids[0].Thumbprint
}
# Any request to the installed/hooked Player over pinned HTTPS. Returns @{ Status; Body }; Status 0 = no answer.
function Invoke-ZephyrTls([string]$method, [string]$path, [string]$token, [string]$body) {
  Set-ZephyrPin
  $r = [ZephyrPin]::Send($method, "https://127.0.0.1:47823$path", $token, $body)
  return @{ Status = [int]$r[0]; Body = $r[1] }
}
function Get-TlsStatus([string]$token) { return (Invoke-ZephyrTls 'GET' '/control/status' $token $null).Status }
