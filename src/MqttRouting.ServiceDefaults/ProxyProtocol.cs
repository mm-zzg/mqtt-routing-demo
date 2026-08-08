using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MqttRouting.ServiceDefaults;

/// <summary>
/// Proxy Protocol v2 (binary) encoder/decoder with client-certificate TLV support.
///
/// Wire format (after standard PPv2 header + address):
///
///   [2 bytes]  tlv_total_len  (total TLV payload bytes that follow; 0 = no TLVs)
///   [ ... ]    TLV payload    (zero or more type-length-value entries)
///
/// Each TLV entry:
///   1 byte   type
///   2 bytes  length (big-endian)
///   N bytes  value
///
/// Custom type 0xE0: client certificate info (from upstream TLS termination).
/// </summary>
public static class ProxyProtocol
{
    private static readonly byte[] Signature =
    {
        0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A
    };

    private const byte VerCmdLocal = 0x20;
    private const byte VerCmdProxy = 0x21;
    private const byte FamilyInetStream  = 0x11;
    private const byte FamilyInet6Stream = 0x21;

    private const byte TlvTypeClientCert = 0xE0;

    // ── Public builder ────────────────────────────────────────────────────

    /// <summary>
    /// Build a complete PPv2 frame (header + address + optional TLVs).
    /// </summary>
    public static byte[] BuildV2Header(EndPoint source, EndPoint destination, X509Certificate2? clientCert = null)
    {
        var header = BuildCoreHeader(source, destination);

        if (clientCert is null)
        {
            // No TLVs → append 2 zero bytes
            var buf = new byte[header.Length + 2];
            Array.Copy(header, 0, buf, 0, header.Length);
            // last 2 bytes remain 0x00 0x00
            return buf;
        }

        var tlv = BuildClientCertTlv(clientCert);

        // Total frame = core header + 2-byte tlv_len + tlv bytes
        var combined = new byte[header.Length + 2 + tlv.Length];
        Array.Copy(header, 0, combined, 0, header.Length);
        BinaryPrimitives.WriteUInt16BigEndian(combined.AsSpan(header.Length, 2), (ushort)tlv.Length);
        Array.Copy(tlv, 0, combined, header.Length + 2, tlv.Length);
        return combined;
    }

    // ── Client certificate TLV encoder ────────────────────────────────────

    private static byte[] BuildClientCertTlv(X509Certificate2 cert)
    {
        var thumbprint = cert.GetCertHash(HashAlgorithmName.SHA256);
        var subject = System.Text.Encoding.UTF8.GetBytes(cert.Subject);
        var issuer  = System.Text.Encoding.UTF8.GetBytes(cert.Issuer);

        // flags(1) + thumbLen(1) + thumb + subjLen(2) + subject + issLen(2) + issuer
        int valueLen = 1 + 1 + thumbprint.Length + 2 + subject.Length + 2 + issuer.Length;

        // type(1) + len(2) + value
        var entry = new byte[1 + 2 + valueLen];
        entry[0] = TlvTypeClientCert;
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(1, 2), (ushort)valueLen);

        int offset = 3;
        entry[offset++] = 0x01;                     // cert_present flag
        entry[offset++] = (byte)thumbprint.Length;
        thumbprint.CopyTo(entry.AsSpan(offset, thumbprint.Length));
        offset += thumbprint.Length;

        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(offset, 2), (ushort)subject.Length);
        offset += 2;
        subject.CopyTo(entry.AsSpan(offset, subject.Length));
        offset += subject.Length;

        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(offset, 2), (ushort)issuer.Length);
        offset += 2;
        issuer.CopyTo(entry.AsSpan(offset, issuer.Length));

        return entry;
    }

    // ── Core header builders (unchanged) ──────────────────────────────────

    private static byte[] BuildCoreHeader(EndPoint source, EndPoint destination)
    {
        if (source is IPEndPoint srcEp && destination is IPEndPoint dstEp)
        {
            if (srcEp.AddressFamily == AddressFamily.InterNetwork &&
                dstEp.AddressFamily == AddressFamily.InterNetwork)
                return BuildV2Tcp4(srcEp, dstEp);

            if (srcEp.AddressFamily == AddressFamily.InterNetworkV6 &&
                dstEp.AddressFamily == AddressFamily.InterNetworkV6)
                return BuildV2Tcp6(srcEp, dstEp);
        }
        return BuildV2Local();
    }

    private static byte[] BuildV2Tcp4(IPEndPoint src, IPEndPoint dst)
    {
        const int addrLen = 12;
        var buffer = new byte[12 + 4 + addrLen];
        Array.Copy(Signature, 0, buffer, 0, 12);
        buffer[12] = VerCmdProxy;
        buffer[13] = FamilyInetStream;
        buffer[14] = (byte)(addrLen >> 8);
        buffer[15] = (byte)addrLen;

        src.Address.TryWriteBytes(buffer.AsSpan(16, 4), out _);
        dst.Address.TryWriteBytes(buffer.AsSpan(20, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(24, 2), (ushort)src.Port);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(26, 2), (ushort)dst.Port);
        return buffer;
    }

    private static byte[] BuildV2Tcp6(IPEndPoint src, IPEndPoint dst)
    {
        const int addrLen = 36;
        var buffer = new byte[12 + 4 + addrLen];
        Array.Copy(Signature, 0, buffer, 0, 12);
        buffer[12] = VerCmdProxy;
        buffer[13] = FamilyInet6Stream;
        buffer[14] = (byte)(addrLen >> 8);
        buffer[15] = (byte)addrLen;

        src.Address.TryWriteBytes(buffer.AsSpan(16, 16), out _);
        dst.Address.TryWriteBytes(buffer.AsSpan(32, 16), out _);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(48, 2), (ushort)src.Port);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(50, 2), (ushort)dst.Port);
        return buffer;
    }

    private static byte[] BuildV2Local()
    {
        var buffer = new byte[16];
        Array.Copy(Signature, 0, buffer, 0, 12);
        buffer[12] = VerCmdLocal;
        return buffer;
    }

    // ── Reader types ──────────────────────────────────────────────────────

    public sealed record ProxyHeaderResult(
        IPEndPoint? Source,
        IPEndPoint? Destination,
        X509CertInfo? ClientCert
    );

    public sealed record X509CertInfo(
        byte[] ThumbprintSha256,
        string Subject,
        string Issuer
    );

    // ── Main reader (PPv2 header + address + TLVs) ────────────────────────

    /// <summary>
    /// Reads a complete PPv2 frame (header + address + optional TLVs).
    /// The stream position after this call points to the first byte of
    /// application data (e.g. MQTT CONNECT).
    /// </summary>
    public static async Task<ProxyHeaderResult> ReadV2HeaderFullAsync(
        NetworkStream stream, CancellationToken ct = default)
    {
        // 1. Signature + ver_cmd + family + addr_len (16 bytes)
        var sig = new byte[16];
        await ReadExactAsync(stream, sig, 0, 16, ct);

        for (int i = 0; i < 12; i++)
            if (sig[i] != Signature[i])
                return new ProxyHeaderResult(null, null, null);

        if (sig[12] == VerCmdLocal)
            return new ProxyHeaderResult(
                new IPEndPoint(IPAddress.None, 0),
                new IPEndPoint(IPAddress.None, 0),
                null);

        if (sig[12] != VerCmdProxy)
            return new ProxyHeaderResult(null, null, null);

        var familyProtocol = sig[13];
        int addrLen = (sig[14] << 8) | sig[15];

        // 2. Address block
        var addrBytes = new byte[addrLen];
        await ReadExactAsync(stream, addrBytes, 0, addrLen, ct);

        var (source, __) = familyProtocol switch
        {
            FamilyInetStream when addrLen >= 12 => (
                new IPEndPoint(new IPAddress(addrBytes.AsSpan(0, 4)),
                    (addrBytes[8] << 8) | addrBytes[9]),
                new IPEndPoint(new IPAddress(addrBytes.AsSpan(4, 4)),
                    (addrBytes[10] << 8) | addrBytes[11])
            ),
            FamilyInet6Stream when addrLen >= 36 => (
                new IPEndPoint(new IPAddress(addrBytes.AsSpan(0, 16)),
                    (addrBytes[32] << 8) | addrBytes[33]),
                new IPEndPoint(new IPAddress(addrBytes.AsSpan(16, 16)),
                    (addrBytes[34] << 8) | addrBytes[35])
            ),
            _ => (null, null)
        };

        // 3. TLV total-length prefix (2 bytes)
        var tlvLenBuf = new byte[2];
        await ReadExactAsync(stream, tlvLenBuf, 0, 2, ct);
        int tlvTotal = BinaryPrimitives.ReadUInt16BigEndian(tlvLenBuf);

        X509CertInfo? clientCert = null;
        if (tlvTotal > 0)
        {
            var tlvData = new byte[tlvTotal];
            await ReadExactAsync(stream, tlvData, 0, tlvTotal, ct);
            clientCert = ParseTlvBlock(tlvData);
        }

        return new ProxyHeaderResult(source, null, clientCert);
    }

    private static X509CertInfo? ParseTlvBlock(ReadOnlySpan<byte> data)
    {
        X509CertInfo? cert = null;
        int offset = 0;

        while (offset + 3 <= data.Length)
        {
            byte type = data[offset];
            int len = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 1, 2));
            offset += 3;

            if (offset + len > data.Length) break;
            var value = data.Slice(offset, len);
            offset += len;

            if (type == TlvTypeClientCert)
                cert = ParseClientCertTlvValue(value);
        }

        return cert;
    }

    private static X509CertInfo? ParseClientCertTlvValue(ReadOnlySpan<byte> value)
    {
        if (value.Length < 2 || value[0] != 0x01) return null;

        int off = 1;
        int thumbLen = value[off++];
        if (off + thumbLen > value.Length) return null;
        var thumbprint = value.Slice(off, thumbLen).ToArray();
        off += thumbLen;

        if (off + 2 > value.Length) return null;
        int subjLen = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(off, 2));
        off += 2;
        if (off + subjLen > value.Length) return null;
        var subject = System.Text.Encoding.UTF8.GetString(value.Slice(off, subjLen));
        off += subjLen;

        if (off + 2 > value.Length) return null;
        int issLen = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(off, 2));
        off += 2;
        if (off + issLen > value.Length) return null;
        var issuer = System.Text.Encoding.UTF8.GetString(value.Slice(off, issLen));

        return new X509CertInfo(thumbprint, subject, issuer);
    }

    // ── Legacy API (standard PPv2 only, no TLVs) ────────────────────────

    /// <summary>
    /// Reads a standard PPv2 header (16 + addr_len bytes), ignoring any TLVs.
    /// Callers that need client cert info should use <see cref="ReadV2HeaderFullAsync"/> instead.
    /// </summary>
    public static async Task<(IPEndPoint? Source, IPEndPoint? Destination)> ReadV2HeaderAsync(
        NetworkStream stream, CancellationToken ct = default)
    {
        var sig = new byte[16];
        await ReadExactAsync(stream, sig, 0, 16, ct);

        for (int i = 0; i < 12; i++)
            if (sig[i] != Signature[i])
                return (null, null);

        if (sig[12] == VerCmdLocal)
            return (new IPEndPoint(IPAddress.None, 0), new IPEndPoint(IPAddress.None, 0));

        if (sig[12] != VerCmdProxy)
            return (null, null);

        var familyProtocol = sig[13];
        int addrLen = (sig[14] << 8) | sig[15];

        var addrBytes = new byte[addrLen];
        await ReadExactAsync(stream, addrBytes, 0, addrLen, ct);

        return familyProtocol switch
        {
            FamilyInetStream when addrLen >= 12 => (
                new IPEndPoint(new IPAddress(addrBytes.AsSpan(0, 4)),
                    (addrBytes[8] << 8) | addrBytes[9]),
                new IPEndPoint(new IPAddress(addrBytes.AsSpan(4, 4)),
                    (addrBytes[10] << 8) | addrBytes[11])
            ),
            FamilyInet6Stream when addrLen >= 36 => (
                new IPEndPoint(new IPAddress(addrBytes.AsSpan(0, 16)),
                    (addrBytes[32] << 8) | addrBytes[33]),
                new IPEndPoint(new IPAddress(addrBytes.AsSpan(16, 16)),
                    (addrBytes[34] << 8) | addrBytes[35])
            ),
            _ => (null, null)
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buf, int off, int cnt, CancellationToken ct)
    {
        int read = 0;
        while (read < cnt)
        {
            int n = await stream.ReadAsync(buf.AsMemory(off + read, cnt - read), ct);
            if (n == 0) throw new EndOfStreamException("Unexpected EOF reading PPv2.");
            read += n;
        }
    }
}
