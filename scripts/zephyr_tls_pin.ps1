# Secure mode is HTTPS only (no plaintext fallback). The setup step above created the Player's own identity in the machine store.
# Requests go over HTTPS to 127.0.0.1 and accept ONLY the certificate whose thumbprint is that stored identity (a pin, not "skip validation").
# Status 0 means no HTTP answer at all (connect or TLS failure) and never satisfies a check below.
$tlsSrc = @(
  'using System; using System.Net.Http; using System.Net.Http.Headers;',
  'public static class ZephyrPin {',
  '  public static string Thumb = "";',
  '  public static int Status(string url, string token) {',
  '    try {',
  '      var h = new HttpClientHandler();',
  '      h.ServerCertificateCustomValidationCallback = (m, c, ch, e) => c != null && Thumb.Length > 0 && string.Equals(c.Thumbprint, Thumb, StringComparison.OrdinalIgnoreCase);',
  '      using (var cl = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(4) }) {',
  '        if (!string.IsNullOrEmpty(token)) cl.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);',
  '        return (int)cl.GetAsync(url).GetAwaiter().GetResult().StatusCode;',
  '      }',
  '    } catch { return 0; }',
  '  }',
  '}') -join "`n"
if (-not ('ZephyrPin' -as [type])) { Add-Type -TypeDefinition $tlsSrc }
function Get-TlsStatus([string]$token) {
  $ids = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq 'CN=Zephyr Player' })
  if ($ids.Count -ne 1) { throw "expected exactly one Zephyr Player identity in the machine store, found $($ids.Count)" }
  [ZephyrPin]::Thumb = $ids[0].Thumbprint
  return [ZephyrPin]::Status('https://127.0.0.1:47823/control/status', $token)
}
