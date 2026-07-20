using System.Text.Json;
using CompanyDlp.BrowserBridge;
using CompanyDlp.Contracts;

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();

while (true)
{
    var lengthBytes = new byte[4];
    if (!await ReadExactAsync(input, lengthBytes, CancellationToken.None)) break;
    var length = BitConverter.ToInt32(lengthBytes, 0);
    if (length is <= 0 or > 4 * 1024 * 1024) break;

    var payload = new byte[length];
    if (!await ReadExactAsync(input, payload, CancellationToken.None)) break;

    object response;
    try
    {
        using var document = JsonDocument.Parse(payload);
        response = await BrowserNativeMessageRouter.HandleAsync(document.RootElement);
    }
    catch (Exception exception)
    {
        response = new { success = false, message = exception.Message };
    }

    var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonDefaults.Options);
    await output.WriteAsync(BitConverter.GetBytes(responseBytes.Length));
    await output.WriteAsync(responseBytes);
    await output.FlushAsync();
}

static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
        if (count == 0) return false;
        offset += count;
    }
    return true;
}
