using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BALLxPITOnlineCoop.Core;

/// <summary>A parsed HTTP/1.1 request head.</summary>
public sealed class HttpRequest
{
    public string Method = "";
    public string Path = "";
    public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);

    public string Header(string name) => Headers.TryGetValue(name, out string? value) ? value : "";

    /// <summary>Reads the request line and headers (no body). Returns null on a malformed or oversized request.</summary>
    public static async Task<HttpRequest?> ReadAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length) return null;
            int read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), token).ConfigureAwait(false);
            if (read <= 0) return null;
            length += read;
            int end = IndexOfHeaderEnd(buffer, length);
            if (end < 0) continue;

            string head = Encoding.ASCII.GetString(buffer, 0, end);
            string[] lines = head.Split("\r\n");
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 3) return null;
            var request = new HttpRequest { Method = requestLine[0], Path = requestLine[1] };
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            return request;
        }
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                return i - 3;
        }
        return -1;
    }
}

public enum WebSocketOpcode : byte
{
    Continuation = 0x0,
    Text = 0x1,
    Binary = 0x2,
    Close = 0x8,
    Ping = 0x9,
    Pong = 0xA,
}

/// <summary>
/// Minimal RFC 6455 server endpoint: text and binary messages, fragmentation, ping/pong and close.
/// Sends are serialised, so any thread may call <see cref="SendAsync"/>.
/// </summary>
public sealed class WebSocketConnection : IDisposable
{
    private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxMessageBytes = 64 * 1024;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _header = new byte[14];
    private byte[] _sendBuffer = new byte[64 * 1024];
    private bool _closed;

    private WebSocketConnection(Stream stream)
    {
        _stream = stream;
    }

    public static bool IsUpgradeRequest(HttpRequest request) =>
        request.Header("Upgrade").Equals("websocket", StringComparison.OrdinalIgnoreCase)
        && request.Header("Connection").IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0
        && request.Header("Sec-WebSocket-Key").Length > 0;

    public static async Task<WebSocketConnection> AcceptAsync(Stream stream, HttpRequest request, CancellationToken token)
    {
        string key = request.Header("Sec-WebSocket-Key");
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Guid)));
        string response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        return new WebSocketConnection(stream);
    }

    /// <summary>Reads the next complete data message. Returns null when the connection closes.</summary>
    public async Task<(WebSocketOpcode Opcode, byte[] Payload)?> ReceiveAsync(CancellationToken token)
    {
        WebSocketOpcode messageOpcode = WebSocketOpcode.Continuation;
        var message = new MemoryStream();
        var head = new byte[8];
        var mask = new byte[4];
        while (true)
        {
            if (!await ReadExactAsync(head, 2, token).ConfigureAwait(false)) return null;
            bool fin = (head[0] & 0x80) != 0;
            var opcode = (WebSocketOpcode)(head[0] & 0x0F);
            bool masked = (head[1] & 0x80) != 0;
            long length = head[1] & 0x7F;
            if (length == 126)
            {
                if (!await ReadExactAsync(head, 2, token).ConfigureAwait(false)) return null;
                length = (head[0] << 8) | head[1];
            }
            else if (length == 127)
            {
                if (!await ReadExactAsync(head, 8, token).ConfigureAwait(false)) return null;
                length = 0;
                for (int i = 0; i < 8; i++) length = (length << 8) | head[i];
            }
            if (!masked || length < 0 || length > MaxMessageBytes) return null; // clients must mask; we only take small messages
            if (!await ReadExactAsync(mask, 4, token).ConfigureAwait(false)) return null;
            var payload = new byte[length];
            if (length > 0 && !await ReadExactAsync(payload, (int)length, token).ConfigureAwait(false)) return null;
            for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

            switch (opcode)
            {
                case WebSocketOpcode.Ping:
                    await SendAsync(WebSocketOpcode.Pong, payload, token).ConfigureAwait(false);
                    continue;
                case WebSocketOpcode.Pong:
                    continue;
                case WebSocketOpcode.Close:
                    await CloseAsync().ConfigureAwait(false);
                    return null;
                case WebSocketOpcode.Text:
                case WebSocketOpcode.Binary:
                    messageOpcode = opcode;
                    message.SetLength(0);
                    break;
                case WebSocketOpcode.Continuation:
                    if (messageOpcode == WebSocketOpcode.Continuation) return null;
                    break;
                default:
                    return null;
            }
            message.Write(payload, 0, payload.Length);
            if (message.Length > MaxMessageBytes) return null;
            if (fin) return (messageOpcode, message.ToArray());
        }
    }

    public Task SendTextAsync(string text, CancellationToken token) =>
        SendAsync(WebSocketOpcode.Text, Encoding.UTF8.GetBytes(text), token);

    public async Task SendAsync(WebSocketOpcode opcode, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await _sendLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_closed) throw new IOException("WebSocket is closed.");
            int headerLength = WriteHeader(opcode, payload.Length);
            int total = headerLength + payload.Length;
            if (_sendBuffer.Length < total) _sendBuffer = new byte[Math.Max(total, _sendBuffer.Length * 2)];
            Buffer.BlockCopy(_header, 0, _sendBuffer, 0, headerLength);
            payload.CopyTo(_sendBuffer.AsMemory(headerLength));
            await _stream.WriteAsync(_sendBuffer.AsMemory(0, total), token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync()
    {
        if (_closed) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await SendAsync(WebSocketOpcode.Close, new byte[] { 0x03, 0xE8 }, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // The peer may already be gone.
        }
        _closed = true;
    }

    private int WriteHeader(WebSocketOpcode opcode, int length)
    {
        _header[0] = (byte)(0x80 | (byte)opcode);
        if (length < 126)
        {
            _header[1] = (byte)length;
            return 2;
        }
        if (length <= 0xFFFF)
        {
            _header[1] = 126;
            _header[2] = (byte)(length >> 8);
            _header[3] = (byte)length;
            return 4;
        }
        _header[1] = 127;
        long l = length;
        for (int i = 0; i < 8; i++) _header[2 + i] = (byte)(l >> (56 - 8 * i));
        return 10;
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, int count, CancellationToken token)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), token).ConfigureAwait(false);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        _closed = true;
        try { _stream.Dispose(); } catch { }
        _sendLock.Dispose();
    }
}
