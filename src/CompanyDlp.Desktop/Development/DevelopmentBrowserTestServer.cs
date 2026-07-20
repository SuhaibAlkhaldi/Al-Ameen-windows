using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CompanyDlp.Desktop.Development;

public sealed class DevelopmentBrowserTestServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;

    public string Url { get; private set; } = "";

    public void Start()
    {
        if (_listener is not null) return;

        _cancellation = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://127.0.0.1:{port}/";
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch
            {
                client?.Dispose();
                if (cancellationToken.IsCancellationRequested) break;
            }
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            try
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrEmpty(line)) break;
                }

                var content = Encoding.UTF8.GetBytes(TestPageHtml);
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken);
                await stream.WriteAsync(content, cancellationToken);
            }
            catch
            {
                // Development-only diagnostics page. Connection failures are non-fatal.
            }
        }
    }

    public void Dispose()
    {
        try { _cancellation?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _listener = null;
        _acceptLoop = null;
        _cancellation?.Dispose();
        _cancellation = null;
        Url = "";
    }

    private const string TestPageHtml = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>Company DLP protected-browser test</title>
  <style>
    body{font-family:Segoe UI,Arial,sans-serif;background:#0f172a;color:#e2e8f0;margin:0;padding:40px}
    main{max-width:820px;margin:auto;background:#172033;border:1px solid #334155;border-radius:18px;padding:28px;box-shadow:0 20px 55px rgba(0,0,0,.35)}
    h1{margin-top:0}.hint{color:#93c5fd}.card{background:#0b1220;border:1px solid #334155;border-radius:14px;padding:20px;margin-top:18px}
    #dropZone{border:2px dashed #64748b;border-radius:14px;padding:45px 20px;text-align:center;margin:16px 0}
    button,input{font:inherit}button{background:#38bdf8;color:#082f49;border:0;border-radius:10px;padding:11px 18px;font-weight:700;cursor:pointer;margin:4px 6px 4px 0}
    #status{min-height:24px;color:#fbbf24;font-weight:600}code{color:#a5f3fc}.ok{color:#4ade80}.bad{color:#f87171}
  </style>
</head>
<body>
<main>
  <h1>Company DLP browser upload test</h1>
  <p class="hint">The managed extension blocks file upload actions on Chrome, Edge and Firefox pages. These tests verify normal picker, hidden showPicker, drag/drop and file paste without treating ordinary background binary traffic as an upload.</p>
  <div class="card">
    <h2>1. File picker / upload</h2>
    <p>Click the file input. Company DLP should block the picker and show one alert.</p>
    <form id="uploadForm">
      <input id="fileInput" name="document" type="file" multiple>
      <button type="submit">Submit selected file</button>
    </form>
  </div>
  <div class="card">
    <h2>2. Hidden picker / showPicker()</h2>
    <p>This simulates sites such as web messengers that use a hidden file input and open it programmatically.</p>
    <input id="hiddenFileInput" type="file" style="display:none">
    <button id="showPickerTest" type="button">Open hidden upload picker</button>
  </div>
  <div class="card">
    <h2>3. Drag and drop anywhere</h2>
    <div id="dropZone">Drag any file from Windows Explorer and drop it here or anywhere on this page.</div>
  </div>
  <div class="card">
    <h2>4. File or image paste</h2>
    <p>Copy a file in Windows Explorer, click this page, then press Ctrl+V. The file must not reach the page.</p>
  </div>
  <div class="card">
    <strong>Page result:</strong> <span id="status">No file action reached the page.</span>
  </div>
</main>
<script>
  const status = document.getElementById('status');
  const pass = message => { status.className = 'ok'; status.textContent = 'PASS: ' + message; };
  const fail = message => { status.className = 'bad'; status.textContent = 'FAIL: ' + message; };

  document.getElementById('fileInput').addEventListener('change', e => {
    e.target.files.length ? fail('the page received selected files.') : pass('no selected files reached the page.');
  });

  const zone = document.getElementById('dropZone');
  ['dragenter','dragover'].forEach(name => zone.addEventListener(name, e => { e.preventDefault(); }));
  zone.addEventListener('drop', e => {
    e.preventDefault();
    e.dataTransfer.files.length ? fail('the page received dropped files.') : pass('no dropped files reached the page.');
  });

  document.getElementById('uploadForm').addEventListener('submit', e => {
    e.preventDefault();
    const files = document.getElementById('fileInput').files;
    files.length ? fail('a form containing files reached the page.') : pass('no file is selected; the upload path stayed blocked.');
  });

  document.getElementById('showPickerTest').addEventListener('click', () => {
    const input = document.getElementById('hiddenFileInput');
    try {
      if (typeof input.showPicker === 'function') input.showPicker();
      else input.click();
      setTimeout(() => {
        input.files.length
          ? fail('the hidden picker delivered a file to the page.')
          : pass('the hidden picker was blocked before a file reached the page.');
      }, 300);
    } catch (error) {
      pass('showPicker was blocked immediately.');
    }
  });

  document.getElementById('hiddenFileInput').addEventListener('change', e => {
    e.target.files.length
      ? fail('the hidden file input received a selected file.')
      : pass('the hidden file input stayed empty.');
  });

  document.addEventListener('paste', e => {
    const count = Math.max(e.clipboardData?.files?.length || 0,
      Array.from(e.clipboardData?.items || []).filter(item => item.kind === 'file').length);
    if (count > 0) fail('the page received a pasted file or image.');
  });
</script>
</body>
</html>
""";
}
