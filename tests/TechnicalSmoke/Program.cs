using System.Buffers.Binary;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using QuickPreview;

internal static class Program
{
    private static int _assertions;
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static async Task<int> Main()
    {
        Directory.CreateDirectory(FixtureRoot);
        try
        {
            CheckTextEncodings();
            await CheckSqlite();
            await CheckPackets();
            await CheckCertificates();
            await CheckPeAndHex();
            await CheckIso();
            await CheckEvents();
            try
            {
                await TechnicalPreviewService.LoadAsync(Path.Combine(FixtureRoot, "cancelled.bin"), new CancellationToken(true));
                throw new Exception("Cancelled request was not cancelled.");
            }
            catch (OperationCanceledException) { Assert(true, "Cancellation is propagated."); }
            Console.WriteLine($"PASS: {_assertions} technical preview assertions.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void CheckTextEncodings()
    {
        const string text = "Windows Registry Editor Version 5.00\r\n[Привет мир]\r\n\"Имя\"=\"Значение\"\r\n";
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, new UTF32Encoding(false, true), new UTF32Encoding(true, true) })
        {
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
            Assert(FormatCatalog.LooksLikeText(bytes, endOfFile: true), $"Detects {encoding.WebName} BOM text.");
            Assert(FormatCatalog.DecodeText(bytes) == text, $"Decodes {encoding.WebName} Cyrillic without BOM artifacts.");
        }
        Assert(FormatCatalog.LooksLikeText(Encoding.Unicode.GetBytes("plain UTF16 text\r\n"), true), "Infers BOM-less UTF16LE from NUL distribution.");
        Assert(FormatCatalog.DecodeText(Encoding.BigEndianUnicode.GetBytes("plain UTF16 text\r\n")) == "plain UTF16 text\r\n", "Decodes BOM-less UTF16BE.");
        Assert(!FormatCatalog.LooksLikeText(new byte[] { 0, 1, 2, 0, 0, 7, 0, 255 }, true), "Rejects binary NULs and controls.");
        Assert(!FormatCatalog.LooksLikeText(new byte[] { 0xC0, 0xAF }, true), "Rejects invalid overlong UTF8.");
        Assert(FormatCatalog.LooksLikeText(new byte[] { 0x61, 0xE2, 0x82 }), "Allows a capped UTF8 sample to end mid-codepoint.");
        Assert(FormatCatalog.IsKnownText("settings.reg") && FormatCatalog.IsKnownText(".env.production") && FormatCatalog.IsKnownText("Dockerfile.dev") && FormatCatalog.IsKnownText("Jenkinsfile"), "Recognizes developer/admin extensionless names.");
        Assert(FormatCatalog.Classify("source.ts") == PreviewKind.Text, "TypeScript remains text, not a transport-stream video.");
    }

    private static async Task CheckSqlite()
    {
        var path = Path.Combine(FixtureRoot, "sample.sqlite");
        if (File.Exists(path)) File.Delete(path);
        var status = SqliteOpen(Encoding.UTF8.GetBytes(path + '\0'), out var db);
        Assert(status == 0, "Creates isolated SQLite fixture.");
        try
        {
            var sql = "CREATE TABLE items(id INTEGER, name TEXT, payload BLOB);" +
                      "INSERT INTO items VALUES(1,'Привет SQLite',X'010203');" +
                      string.Concat(Enumerable.Range(2, 15).Select(index => $"INSERT INTO items VALUES({index},'row {index}',NULL);")) +
                      "CREATE TABLE \"odd\"\"table\"(value TEXT); INSERT INTO \"odd\"\"table\" VALUES('quoted identifier works');" +
                      "CREATE VIEW never_execute AS SELECT load_extension('nonexistent-preview-test');";
            var result = SqliteExec(db, Encoding.UTF8.GetBytes(sql + '\0'), IntPtr.Zero, IntPtr.Zero, out var error);
            if (error != IntPtr.Zero) SqliteFree(error);
            Assert(result == 0, "Creates rows, quoted identifiers and a view that must not execute.");
        }
        finally { SqliteClose(db); }
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var modified = File.GetLastWriteTimeUtc(path);
        var preview = await TechnicalPreviewService.LoadAsync(path);
        Assert(preview.Summary.StartsWith("SQLite") && preview.Text.Contains("Привет SQLite"), "SQLite preview renders real schema and UTF8 rows.");
        Assert(preview.Text.Contains("quoted identifier works") && preview.Text.Contains("<BLOB 3 байт>"), "SQLite safely quotes identifiers and summarizes blobs.");
        Assert(preview.Text.Contains("never_execute") && !preview.Summary.StartsWith("Hex"), "Views are displayed without evaluating load_extension.");
        Assert(preview.IsTruncated && !preview.Text.Contains("row 15 |"), "SQLite row preview is bounded.");
        Assert(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))) && modified == File.GetLastWriteTimeUtc(path), "SQLite source content and modification time stay unchanged.");
    }

    private static async Task CheckPackets()
    {
        var packet = new byte[42];
        packet[12] = 0x08; packet[13] = 0x00; packet[14] = 0x45; packet[23] = 17;
        new byte[] { 192, 0, 2, 1 }.CopyTo(packet, 26);
        new byte[] { 198, 51, 100, 2 }.CopyTo(packet, 30);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(34), 12345);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(36), 53);
        foreach (var big in new[] { false, true })
        {
            var path = Path.Combine(FixtureRoot, big ? "big.pcap" : "little.pcap");
            using (var stream = File.Create(path))
            {
                Write32(stream, 0xA1B2C3D4, big); Write16(stream, 2, big); Write16(stream, 4, big);
                Write32(stream, 0, big); Write32(stream, 0, big); Write32(stream, 65535, big); Write32(stream, 1, big);
                Write32(stream, 1700000000, big); Write32(stream, 123456, big); Write32(stream, (uint)packet.Length, big); Write32(stream, (uint)packet.Length, big);
                stream.Write(packet);
            }
            var preview = await TechnicalPreviewService.LoadAsync(path);
            Assert(preview.Summary.StartsWith("PCAP") && preview.Text.Contains("UDP 192.0.2.1:12345 → 198.51.100.2:53"), $"PCAP {(big ? "big" : "little")} endian decodes packet header and endpoints.");
        }
        var ng = Path.Combine(FixtureRoot, "sample.pcapng");
        using (var stream = File.Create(ng))
        {
            Write32(stream, 0x0A0D0D0A); Write32(stream, 28); Write32(stream, 0x1A2B3C4D); Write16(stream, 1); Write16(stream, 0);
            Write32(stream, uint.MaxValue); Write32(stream, uint.MaxValue); Write32(stream, 28);
            Write32(stream, 1); Write32(stream, 20); Write16(stream, 1); Write16(stream, 0); Write32(stream, 65535); Write32(stream, 20);
            var blockLength = 32u + (uint)((packet.Length + 3) & ~3);
            var stamp = 1700000000123456UL;
            Write32(stream, 6); Write32(stream, blockLength); Write32(stream, 0); Write32(stream, (uint)(stamp >> 32)); Write32(stream, (uint)stamp);
            Write32(stream, (uint)packet.Length); Write32(stream, (uint)packet.Length); stream.Write(packet); stream.Write(new byte[2]); Write32(stream, blockLength);
        }
        var ngPreview = await TechnicalPreviewService.LoadAsync(ng);
        Assert(ngPreview.Summary.StartsWith("PCAPNG") && ngPreview.Text.Contains("Интерфейс 0: Ethernet") && ngPreview.Text.Contains("192.0.2.1:12345"), "PCAPNG resolves interface and enhanced packet block.");
        var bad = Path.Combine(FixtureRoot, "bad.pcapng");
        File.WriteAllBytes(bad, new byte[] { 0x0A, 0x0D, 0x0D, 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 });
        Assert((await TechnicalPreviewService.LoadAsync(bad)).Summary.StartsWith("Hex"), "Malformed capture fails safely to bounded hex.");
    }

    private static async Task CheckCertificates()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=QuickPreview local fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(FixtureRoot, "sample.cer");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        var preview = await TechnicalPreviewService.LoadAsync(path);
        Assert(preview.Summary.StartsWith("X.509") && preview.Text.Contains("QuickPreview local fixture") && preview.Text.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256)), "Certificate preview shows real subject and SHA256 fingerprint.");
        var pem = Path.Combine(FixtureRoot, "private-only.pem");
        File.WriteAllText(pem, new string(PemEncoding.Write("PRIVATE KEY", key.ExportPkcs8PrivateKey())));
        var privatePreview = await TechnicalPreviewService.LoadAsync(pem);
        Assert(privatePreview.Summary.StartsWith("PEM") && !privatePreview.Text.Contains("BEGIN PRIVATE KEY") && !privatePreview.Text.Contains("MII"), "Private-only PEM does not dump secret key material.");
        var pfx = Path.Combine(FixtureRoot, "protected.pfx");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, "fixture-password"));
        var protectedPreview = await TechnicalPreviewService.LoadAsync(pfx);
        Assert(protectedPreview.Summary.StartsWith("PKCS#12") && protectedPreview.Text.Contains("парол"), "Password-protected PFX provides an accurate local limitation.");
    }

    private static async Task CheckPeAndHex()
    {
        var path = Path.Combine(FixtureRoot, "synthetic.dll");
        var bytes = new byte[1024];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3C), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x86), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x94), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x2022);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x98), 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xA8), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xD0), 8192);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0xDC), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x104), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x110), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x114), 40);
        Encoding.ASCII.GetBytes(".text").CopyTo(bytes, 0x188);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x190), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x194), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x198), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x19C), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x20C), 0x1040);
        Encoding.ASCII.GetBytes("KERNEL32.dll\0").CopyTo(bytes, 0x240);
        File.WriteAllBytes(path, bytes);
        var preview = await TechnicalPreviewService.LoadAsync(path);
        Assert(preview.Summary.StartsWith("PE") && preview.Text.Contains("x64") && preview.Text.Contains(".text") && preview.Text.Contains("KERNEL32.dll"), "PE inspection reads architecture, sections and import DLL names without loading them.");
        var binary = Path.Combine(FixtureRoot, "unknown.bin");
        File.WriteAllBytes(binary, Enumerable.Range(0, 10000).Select(value => (byte)value).ToArray());
        var hex = await TechnicalPreviewService.LoadAsync(binary);
        Assert(hex.IsTruncated && hex.Text.Contains("00000FF0") && !hex.Text.Contains("00001000"), "Unknown binary preview is bounded at 4096 bytes.");
        File.WriteAllBytes(binary, Array.Empty<byte>());
        Assert((await TechnicalPreviewService.LoadAsync(binary)).Text.Contains("Файл пуст"), "Empty binary file preview works.");
    }

    private static async Task CheckEvents()
    {
        var malformed = Path.Combine(FixtureRoot, "malformed.evtx");
        File.WriteAllBytes(malformed, Encoding.ASCII.GetBytes("ElfFile\0invalid"));
        Assert((await TechnicalPreviewService.LoadAsync(malformed)).Summary.StartsWith("Hex"), "Malformed EVTX fails safely.");
        var exported = Path.Combine(FixtureRoot, "empty-events.evtx");
        try
        {
            if (File.Exists(exported)) File.Delete(exported);
            using var session = new EventLogSession();
            session.ExportLog("Application", PathType.LogName, "*[System[EventID=65535]]", exported);
            var preview = await TechnicalPreviewService.LoadAsync(exported);
            Assert(preview.Summary.StartsWith("EVTX"), "OS EventLogReader reads a real exported EVTX archive.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or EventLogException)
        {
            Console.WriteLine("SKIP: real EVTX export is unavailable in this sandbox/session: " + exception.GetType().Name);
        }
    }

    private static async Task CheckIso()
    {
        foreach (var unicode in new[] { false, true })
        {
            var bytes = CreateIsoFixture(unicode);
            var path = Path.Combine(FixtureRoot, unicode ? "joliet.img" : "sample.iso");
            File.WriteAllBytes(path, bytes);
            var before = SHA256.HashData(bytes);
            var preview = await TechnicalPreviewService.LoadAsync(path);
            Assert(preview.Summary.Contains("ISO9660") && preview.Text.Contains(unicode ? "Привет.txt" : "HELLO.TXT") && preview.Text.Contains("123 байт"), $"{(unicode ? "Joliet Unicode" : "ISO9660")} lists actual root filenames and sizes without mounting.");
            Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "ISO source is unchanged.");
        }
        var unknown = Path.Combine(FixtureRoot, "not-iso.img");
        File.WriteAllBytes(unknown, new byte[40000]);
        Assert((await TechnicalPreviewService.LoadAsync(unknown)).Summary.StartsWith("Hex"), "Unrecognized disk image is accurately shown as hex.");
    }

    private static byte[] CreateIsoFixture(bool unicode)
    {
        var bytes = new byte[24 * 2048];
        for (var sector = 16; sector <= 18; sector++)
        {
            var descriptor = bytes.AsSpan(sector * 2048, 2048);
            descriptor[0] = sector == 16 ? (byte)1 : sector == 17 && unicode ? (byte)2 : (byte)255;
            Encoding.ASCII.GetBytes("CD001").CopyTo(descriptor[1..]); descriptor[6] = 1;
            if (descriptor[0] == 255) continue;
            var useUnicode = descriptor[0] == 2;
            if (useUnicode) { descriptor[88] = (byte)'%'; descriptor[89] = (byte)'/'; descriptor[90] = (byte)'E'; }
            var label = useUnicode ? Encoding.BigEndianUnicode.GetBytes("ТЕСТ") : Encoding.ASCII.GetBytes("TESTVOL");
            label.CopyTo(descriptor[40..]);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[80..], 24); BinaryPrimitives.WriteUInt32BigEndian(descriptor[84..], 24);
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor[128..], 2048); BinaryPrimitives.WriteUInt16BigEndian(descriptor[130..], 2048);
            WriteIsoRecord(descriptor[156..], new byte[] { 0 }, 2, 20, 2048);
        }
        var root = bytes.AsSpan(20 * 2048, 2048);
        var offset = WriteIsoRecord(root, new byte[] { 0 }, 2, 20, 2048);
        offset += WriteIsoRecord(root[offset..], new byte[] { 1 }, 2, 20, 2048);
        var name = unicode ? Encoding.BigEndianUnicode.GetBytes("Привет.txt;1") : Encoding.ASCII.GetBytes("HELLO.TXT;1");
        offset += WriteIsoRecord(root[offset..], name, 0, 21, 123);
        WriteIsoRecord(root[offset..], unicode ? Encoding.BigEndianUnicode.GetBytes("Папка") : Encoding.ASCII.GetBytes("FOLDER"), 2, 22, 2048);
        return bytes;
    }

    private static int WriteIsoRecord(Span<byte> record, byte[] name, byte flags, uint extent, uint length)
    {
        var recordLength = (33 + name.Length + 1) & ~1;
        record[0] = (byte)recordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(record[2..], extent); BinaryPrimitives.WriteUInt32BigEndian(record[6..], extent);
        BinaryPrimitives.WriteUInt32LittleEndian(record[10..], length); BinaryPrimitives.WriteUInt32BigEndian(record[14..], length);
        record[25] = flags; record[28] = 1; record[31] = 1; record[32] = (byte)name.Length;
        name.CopyTo(record[33..]);
        return recordLength;
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
        _assertions++;
        Console.WriteLine("PASS: " + message);
    }
    private static void Write16(Stream stream, ushort value, bool big = false)
    {
        Span<byte> bytes = stackalloc byte[2];
        if (big) BinaryPrimitives.WriteUInt16BigEndian(bytes, value); else BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }
    private static void Write32(Stream stream, uint value, bool big = false)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (big) BinaryPrimitives.WriteUInt32BigEndian(bytes, value); else BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteOpen(byte[] path, out IntPtr db);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteClose(IntPtr db);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteExec(IntPtr db, byte[] sql, IntPtr callback, IntPtr data, out IntPtr error);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SqliteFree(IntPtr pointer);
}
