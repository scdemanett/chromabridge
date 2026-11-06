// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ChromaBridge.Service;

/// <summary>
/// The named pipe the tray app talks to. The service is elevated and the tray app is not,
/// so the pipe is opened to the logged-in user explicitly, and the only things a client can
/// do are read status and change lighting settings.
/// </summary>
internal sealed class ControlPipeServer
{
    private const int MaxClients = 4;

    private readonly Func<BridgeMessage, BridgeMessage> _handle;

    public ControlPipeServer(Func<BridgeMessage, BridgeMessage> handle)
    {
        _handle = handle;
    }

    public void Start(CancellationToken stoppingToken)
    {
        for (var i = 0; i < MaxClients; i++)
            _ = Task.Run(() => ListenAsync(stoppingToken), stoppingToken);
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();

                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await ServeAsync(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The client went away; wait for the next one
            }
            catch (Exception ex)
            {
                Log.Error("Control pipe failed", ex);

                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);

        while (!stoppingToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false);

            if (line == null)
                return;

            var request = BridgeJson.TryParseMessage(line);

            var reply = request == null
                ? new BridgeMessage { Type = MessageTypes.Error, Error = "Not a valid message." }
                : _handle(request);

            await writer.WriteLineAsync(BridgeJson.Serialize(reply).AsMemory(), stoppingToken).ConfigureAwait(false);
            await writer.FlushAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        using var identity = WindowsIdentity.GetCurrent();

        // The same user, whether or not their process is elevated
        if (identity.User != null)
            security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // Whoever owns this process may create the other pipe instances: Administrators when
        // elevated (so the unelevated user cannot open instances of their own), the user otherwise
        if (identity.Owner != null)
            security.AddAccessRule(new PipeAccessRule(identity.Owner, PipeAccessRights.FullControl, AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            BridgePaths.ControlPipeName,
            PipeDirection.InOut,
            MaxClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            security);
    }
}
