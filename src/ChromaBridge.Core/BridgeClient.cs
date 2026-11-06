// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.IO.Pipes;
using System.Text;

namespace ChromaBridge;

/// <summary>
/// The tray app's connection to the service. One request, one reply, over a named pipe.
/// Not thread safe: callers send one request at a time.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// Sends a request and returns the reply, or null when the service cannot be reached.
    /// </summary>
    public async Task<BridgeMessage?> SendAsync(BridgeMessage request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsConnected)
                await ConnectAsync(cancellationToken).ConfigureAwait(false);

            await _writer!.WriteLineAsync(BridgeJson.Serialize(request).AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            var line = await _reader!.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line == null)
            {
                Disconnect();
                return null;
            }

            return BridgeJson.TryParseMessage(line);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Disconnect();
            return null;
        }
    }

    public Task<BridgeMessage?> GetStatusAsync(CancellationToken cancellationToken = default) => SendAsync(new BridgeMessage { Type = MessageTypes.GetStatus }, cancellationToken);

    public Task<BridgeMessage?> GetSettingsAsync(CancellationToken cancellationToken = default) => SendAsync(new BridgeMessage { Type = MessageTypes.GetSettings }, cancellationToken);

    public Task<BridgeMessage?> SetSettingsAsync(BridgeSettings settings, CancellationToken cancellationToken = default) => SendAsync(new BridgeMessage { Type = MessageTypes.SetSettings, Settings = settings }, cancellationToken);

    public Task<BridgeMessage?> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => SendAsync(new BridgeMessage { Type = MessageTypes.CheckForUpdates }, cancellationToken);

    public Task<BridgeMessage?> InstallUpdateAsync(CancellationToken cancellationToken = default) => SendAsync(new BridgeMessage { Type = MessageTypes.InstallUpdate }, cancellationToken);

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        Disconnect();

        var pipe = new NamedPipeClientStream(".", BridgePaths.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
    }

    private void Disconnect()
    {
        _reader?.Dispose();
        _writer = null;
        _reader = null;
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose() => Disconnect();
}
