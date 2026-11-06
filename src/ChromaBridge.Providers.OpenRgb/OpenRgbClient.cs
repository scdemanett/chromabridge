// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace ChromaBridge.Providers.OpenRgb;

/// <summary>
/// One controller as OpenRGB describes it.
/// </summary>
internal sealed record OpenRgbController(int Index, string Name, string Vendor, int LedCount);

/// <summary>
/// A client for the OpenRGB SDK server (TCP, port 6742 by default). Implements the part of
/// the protocol a lighting bridge needs: list the controllers and set their LED colors.
///
/// It speaks protocol versions 1 to 5, which address controllers by index. Version 6 changes
/// the addressing and the data layout and is not released, so the client never asks for it.
/// </summary>
internal sealed class OpenRgbClient : IDisposable
{
    public const int DefaultPort = 6742;

    private const uint MaxProtocolVersion = 5;

    private const uint PacketRequestControllerCount = 0;
    private const uint PacketRequestControllerData = 1;
    private const uint PacketRequestProtocolVersion = 40;
    private const uint PacketSetClientName = 50;
    private const uint PacketDeviceListUpdated = 100;
    private const uint PacketSetCustomMode = 1100;
    private const uint PacketUpdateLeds = 1050;

    private const int HeaderSize = 16;
    private const int MaxPacketSize = 16 * 1024 * 1024;

    private static readonly byte[] s_magic = "ORGB"u8.ToArray();

    private readonly object _sendLock = new();
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    private Thread? _reader;
    private volatile bool _deviceListChanged;
    private volatile bool _closed;

    public uint ProtocolVersion { get; private set; }

    /// <summary>True once the server said its device list changed: indexes are no longer valid.</summary>
    public bool DeviceListChanged => _deviceListChanged;

    /// <summary>True once the connection is gone.</summary>
    public bool Closed => _closed;

    private OpenRgbClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>
    /// Connects and agrees a protocol version. Throws <see cref="SocketException"/> or
    /// <see cref="IOException"/> when the server is not there.
    /// </summary>
    public static OpenRgbClient Connect(string host, int port, string clientName)
    {
        var tcp = new TcpClient { NoDelay = true };

        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(3)))
                throw new IOException($"Timed out connecting to {host}:{port}.");

            tcp.ReceiveTimeout = 5000;
            tcp.SendTimeout = 5000;

            var client = new OpenRgbClient(tcp);

            client.Send(0, PacketSetClientName, Encoding.ASCII.GetBytes(clientName + "\0"));

            Span<byte> version = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(version, MaxProtocolVersion);
            client.Send(0, PacketRequestProtocolVersion, version);

            var reply = client.ReadReply(PacketRequestProtocolVersion);

            if (reply.Length < 4)
                throw new IOException("OpenRGB sent an invalid protocol version reply.");

            client.ProtocolVersion = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(reply), MaxProtocolVersion);

            if (client.ProtocolVersion == 0)
                throw new IOException("This OpenRGB version is too old (SDK protocol 0). Use OpenRGB 0.5 or later.");

            return client;
        }
        catch (AggregateException ex) when (ex.InnerException != null)
        {
            tcp.Dispose();
            throw ex.InnerException;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public List<OpenRgbController> GetControllers()
    {
        Send(0, PacketRequestControllerCount, []);

        var countReply = ReadReply(PacketRequestControllerCount);

        if (countReply.Length < 4)
            throw new IOException("OpenRGB sent an invalid controller count reply.");

        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(countReply);
        var controllers = new List<OpenRgbController>(count);

        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, ProtocolVersion);

        for (var i = 0; i < count; i++)
        {
            Send((uint)i, PacketRequestControllerData, version);

            controllers.Add(ParseController(i, ReadReply(PacketRequestControllerData), ProtocolVersion));
        }

        return controllers;
    }

    /// <summary>Puts a controller into its direct or custom mode, where it shows the colors it is sent.</summary>
    public void SetCustomMode(int index) => Send((uint)index, PacketSetCustomMode, []);

    public void UpdateLeds(int index, ReadOnlySpan<Rgb> colors)
    {
        // data_size, num_colors, then one RGBColor (R, G, B, 0) per LED
        var size = 4 + 2 + (colors.Length * 4);
        var data = new byte[size];

        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)colors.Length);

        for (var i = 0; i < colors.Length; i++)
        {
            var offset = 6 + (i * 4);

            data[offset] = colors[i].R;
            data[offset + 1] = colors[i].G;
            data[offset + 2] = colors[i].B;
        }

        Send((uint)index, PacketUpdateLeds, data);
    }

    /// <summary>
    /// Starts reading what the server sends on its own, so a device list change or a dropped
    /// connection is noticed. Call once the request and reply exchanges are done.
    /// </summary>
    public void StartListening()
    {
        _tcp.ReceiveTimeout = 0;
        _reader = new Thread(Listen) { IsBackground = true, Name = "OpenRGB Reader" };
        _reader.Start();
    }

    public void Dispose()
    {
        _closed = true;
        _tcp.Dispose();
    }

    /// <summary>
    /// Reads the controller data block far enough to get the name, the vendor and the LED
    /// count. The block has no offsets, so every field before them has to be walked.
    /// </summary>
    internal static OpenRgbController ParseController(int index, byte[] data, uint version)
    {
        var reader = new BlockReader(data);

        reader.Skip(4); // data_size
        reader.Skip(4); // type

        var name = reader.ReadString();
        var vendor = version >= 1 ? reader.ReadString() : "";

        reader.SkipString(); // description
        reader.SkipString(); // version
        reader.SkipString(); // serial
        reader.SkipString(); // location

        var modeCount = reader.ReadUInt16();

        reader.Skip(4); // active_mode

        for (var i = 0; i < modeCount; i++)
        {
            reader.SkipString(); // mode_name
            reader.Skip(4);      // mode_value
            reader.Skip(4 * 3);  // flags, speed_min, speed_max

            if (version >= 3)
                reader.Skip(4 * 2); // brightness_min, brightness_max

            reader.Skip(4 * 3); // colors_min, colors_max, speed

            if (version >= 3)
                reader.Skip(4); // brightness

            reader.Skip(4 * 2); // direction, color_mode
            reader.Skip(4 * reader.ReadUInt16()); // mode colors
        }

        var zoneCount = reader.ReadUInt16();

        for (var i = 0; i < zoneCount; i++)
        {
            reader.SkipString(); // zone_name
            reader.Skip(4 * 4);  // type, leds_min, leds_max, leds_count
            reader.Skip(reader.ReadUInt16()); // matrix map

            if (version >= 4)
            {
                var segmentCount = reader.ReadUInt16();

                for (var j = 0; j < segmentCount; j++)
                {
                    reader.SkipString(); // segment_name
                    reader.Skip(4 * 3);  // type, start_idx, leds_count
                }
            }

            if (version >= 5)
                reader.Skip(4); // zone_flags
        }

        var ledCount = reader.ReadUInt16();

        return new OpenRgbController(index, name.Length > 0 ? name : $"OpenRGB Device {index + 1}", vendor, ledCount);
    }

    private void Send(uint deviceIndex, uint packetId, ReadOnlySpan<byte> data)
    {
        var packet = new byte[HeaderSize + data.Length];

        s_magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)data.Length);
        data.CopyTo(packet.AsSpan(HeaderSize));

        // Devices are written from several threads over the one connection
        lock (_sendLock)
            _stream.Write(packet);
    }

    /// <summary>Reads packets until the one being waited for arrives.</summary>
    private byte[] ReadReply(uint packetId)
    {
        while (true)
        {
            var (id, data) = ReadPacket();

            if (id == packetId)
                return data;

            if (id == PacketDeviceListUpdated)
                _deviceListChanged = true;
        }
    }

    private (uint Id, byte[] Data) ReadPacket()
    {
        var header = new byte[HeaderSize];

        _stream.ReadExactly(header);

        if (!header.AsSpan(0, 4).SequenceEqual(s_magic))
            throw new IOException("OpenRGB sent a packet that does not start with ORGB.");

        var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));

        if (size > MaxPacketSize)
            throw new IOException($"OpenRGB sent an implausibly large packet ({size} bytes).");

        var data = new byte[size];

        _stream.ReadExactly(data);

        return (id, data);
    }

    private void Listen()
    {
        try
        {
            while (!_closed)
            {
                var (id, _) = ReadPacket();

                if (id == PacketDeviceListUpdated)
                    _deviceListChanged = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _closed = true;
        }
    }

    /// <summary>Walks a little-endian block; strings are a 16-bit length, then bytes with a trailing zero.</summary>
    private ref struct BlockReader(byte[] data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public void Skip(int count)
        {
            if (count < 0 || _position + count > _data.Length)
                throw new IOException("OpenRGB sent controller data that ends early.");

            _position += count;
        }

        public ushort ReadUInt16()
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

            return value;
        }

        public string ReadString()
        {
            var bytes = Take(ReadUInt16());
            var end = bytes.IndexOf((byte)0);

            return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]).Trim();
        }

        public void SkipString() => Skip(ReadUInt16());

        private ReadOnlySpan<byte> Take(int count)
        {
            var start = _position;

            Skip(count);

            return _data.Slice(start, count);
        }
    }
}
