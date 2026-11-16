using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QuickPreview;

internal sealed record TechnicalPreviewResult(string Text, string Summary, bool IsTruncated = false);

/// <summary>Local data inspection only: never loads executable code or performs network requests.</summary>
internal static class TechnicalPreviewService
{
    private const int MaxPackets = 128;
    private const int MaxEvents = 64;
    private const int MaxHexBytes = 4096;
    private static readonly HashSet<string> CaptureExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pcap", ".pcapng", ".cap" };
    private static readonly HashSet<string> CertificateExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cer", ".crt", ".der", ".pem", ".pfx", ".p12", ".p7b", ".p7c" };

    public static bool Supports(string path) => FormatCatalog.TechnicalExtensions.Contains(Path.GetExtension(path));

    public static Task<TechnicalPreviewResult> LoadAsync(string path, CancellationToken token = default) =>
        Task.Run(() => Load(path, token), token);

    private static TechnicalPreviewResult Load(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = OpenRead(path);
        var prefix = ReadBytes(stream, (int)Math.Min(32, stream.Length));
        var extension = Path.GetExtension(path);
        try
        {
            if (prefix.AsSpan().StartsWith(Encoding.ASCII.GetBytes("SQLite format 3\0")))
                return LoadSqlite(path, prefix, token);
            if (extension.Equals(".evtx", StringComparison.OrdinalIgnoreCase))
                return LoadEvents(path, token);
            if (CaptureExtensions.Contains(extension) || IsPcap(prefix) || IsPcapNg(prefix))
            {
                stream.Position = 0;
                return IsPcapNg(prefix) ? LoadPcapNg(stream, token) : LoadPcap(stream, token);
            }
            if (prefix.Length >= 2 && prefix[0] == 'M' && prefix[1] == 'Z')
            {
                stream.Position = 0;
                return LoadPortableExecutable(path, stream, token);
            }
            if (CertificateExtensions.Contains(extension))
                return LoadCertificates(stream, token);
            if (extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) || extension.Equals(".img", StringComparison.OrdinalIgnoreCase))
                return LoadIso(stream, token) ?? Hex(stream, "Сигнатура ISO9660 / Joliet в образе не найдена. Другие файловые системы не монтируются.", token);
            return Hex(stream, null, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or
            CryptographicException or EventLogException or DllNotFoundException or EntryPointNotFoundException or ArgumentException or OverflowException)
        {
            if (CertificateExtensions.Contains(extension))
                return new($"Не удалось прочитать открытые метаданные сертификата.\n{Clip(exception.Message, 300)}\n\nЗакрытые ключи и содержимое контейнера не показываются.", "X.509 • метаданные недоступны");
            return Hex(stream, $"Не удалось разобрать формат: {Clip(exception.Message, 300)}", token);
        }
    }

    private static TechnicalPreviewResult LoadSqlite(string path, byte[] header, CancellationToken token)
    {
        var db = IntPtr.Zero;
        var elapsed = Stopwatch.StartNew();
        Sqlite.ProgressCallback progress = _ => token.IsCancellationRequested || elapsed.Elapsed > TimeSpan.FromSeconds(3) ? 1 : 0;
        try
        {
            // No CREATE/READWRITE/URI/SHAREDCACHE flags; extensions remain disabled.
            var status = Sqlite.sqlite3_open_v2(Encoding.UTF8.GetBytes(Path.GetFullPath(path) + '\0'), out db,
                0x00000001 | 0x00008000 | 0x00040000, IntPtr.Zero);
            if (status != 0) throw new InvalidDataException(Sqlite.Error(db));
            Sqlite.sqlite3_busy_timeout(db, 100);
            Sqlite.sqlite3_limit(db, 0, 2 * 1024 * 1024); // SQLITE_LIMIT_LENGTH
            Sqlite.sqlite3_limit(db, 1, 256 * 1024); // SQL length (schema statements included)
            Sqlite.sqlite3_limit(db, 2, 256); // columns
            Sqlite.sqlite3_limit(db, 3, 64); // expression depth
            Sqlite.sqlite3_progress_handler(db, 1000, progress, IntPtr.Zero);
            Execute(db, "PRAGMA query_only=ON;", token);
            Execute(db, "PRAGMA trusted_schema=OFF;", token);
            var text = new StringBuilder("База данных SQLite — только чтение\n");
            var pageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16, 2));
            text.AppendLine($"Размер страницы: {(pageSize == 1 ? 65536 : pageSize)} байт");
            text.AppendLine("До 8 обычных таблиц, до 10 строк в каждой. Представления и виртуальные таблицы не выполняются.\n");
            var schema = Query(db, "SELECT name,type,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name LIMIT 33;", 33, token);
            var truncated = schema.Count > 32;
            var tableCount = 0;
            foreach (var row in schema.Take(32))
            {
                token.ThrowIfCancellationRequested();
                var name = row[0];
                var type = row[1];
                var sql = row[2];
                text.AppendLine($"{type}: {name}");
                text.AppendLine(Clip(sql, 1500));
                if (type != "table" || !IsOrdinarySqliteTable(sql))
                {
                    text.AppendLine();
                    continue;
                }
                if (++tableCount > 8) { truncated = true; text.AppendLine("Данные этой таблицы не загружены.\n"); continue; }
                var quoted = '"' + name.Replace("\"", "\"\"") + '"';
                var statement = Prepare(db, $"SELECT * FROM {quoted} LIMIT 11;");
                try
                {
                    var columns = Sqlite.sqlite3_column_count(statement);
                    text.AppendLine(string.Join(" | ", Enumerable.Range(0, columns).Select(index =>
                        Clip(Marshal.PtrToStringUTF8(Sqlite.sqlite3_column_name(statement, index)) ?? "?", 100))));
                    var count = 0;
                    int step;
                    while ((step = Sqlite.sqlite3_step(statement)) == 100)
                    {
                        token.ThrowIfCancellationRequested();
                        if (++count > 10) { truncated = true; text.AppendLine("… показаны первые 10 строк"); break; }
                        text.AppendLine(string.Join(" | ", Enumerable.Range(0, columns).Select(index => Sqlite.Value(statement, index))));
                    }
                    if (step is not (100 or 101)) throw new InvalidDataException(Sqlite.Error(db));
                    if (count == 0) text.AppendLine("(таблица пуста)");
                }
                finally { Sqlite.sqlite3_finalize(statement); }
                text.AppendLine();
                if (text.Length > 128 * 1024) { truncated = true; break; }
            }
            if (schema.Count == 0) text.AppendLine("Пользовательских таблиц нет.");
            if (truncated) text.AppendLine("\nПредпросмотр ограничен; исходная база не изменена.");
            return new(text.ToString(), "SQLite • схема и первые строки • только чтение", truncated);
        }
        finally
        {
            if (db != IntPtr.Zero)
            {
                Sqlite.sqlite3_progress_handler(db, 0, null, IntPtr.Zero);
                Sqlite.sqlite3_close(db);
            }
            GC.KeepAlive(progress);
        }
    }

    private static void Execute(IntPtr db, string sql, CancellationToken token) => Query(db, sql, 0, token);

    private static List<string[]> Query(IntPtr db, string sql, int maxRows, CancellationToken token)
    {
        var statement = Prepare(db, sql);
        try
        {
            var rows = new List<string[]>();
            int status;
            while ((status = Sqlite.sqlite3_step(statement)) == 100)
            {
                token.ThrowIfCancellationRequested();
                if (rows.Count >= maxRows) break;
                var columns = Sqlite.sqlite3_column_count(statement);
                rows.Add(Enumerable.Range(0, columns).Select(index => Sqlite.SchemaValue(statement, index)).ToArray());
            }
            if (status is not (100 or 101)) throw new InvalidDataException(Sqlite.Error(db));
            return rows;
        }
        finally { Sqlite.sqlite3_finalize(statement); }
    }

    private static IntPtr Prepare(IntPtr db, string sql)
    {
        var bytes = Encoding.UTF8.GetBytes(sql + '\0');
        var status = Sqlite.sqlite3_prepare_v2(db, bytes, bytes.Length, out var statement, IntPtr.Zero);
        if (status != 0)
        {
            if (statement != IntPtr.Zero) Sqlite.sqlite3_finalize(statement);
            throw new InvalidDataException(Sqlite.Error(db));
        }
        return statement;
    }

    private static bool IsOrdinarySqliteTable(string sql)
    {
        var position = 0;
        return NextWord().Equals("CREATE", StringComparison.OrdinalIgnoreCase) &&
               NextWord().Equals("TABLE", StringComparison.OrdinalIgnoreCase);

        string NextWord()
        {
            while (position < sql.Length)
            {
                if (char.IsWhiteSpace(sql[position])) { position++; continue; }
                if (position + 1 < sql.Length && sql[position] == '-' && sql[position + 1] == '-')
                {
                    var end = sql.IndexOf('\n', position + 2);
                    position = end < 0 ? sql.Length : end + 1;
                    continue;
                }
                if (position + 1 < sql.Length && sql[position] == '/' && sql[position + 1] == '*')
                {
                    var end = sql.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    position = end < 0 ? sql.Length : end + 2;
                    continue;
                }
                break;
            }
            var start = position;
            while (position < sql.Length && (sql[position] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) position++;
            return sql[start..position];
        }
    }

    private static TechnicalPreviewResult LoadEvents(string path, CancellationToken token)
    {
        var query = new EventLogQuery(path, PathType.FilePath) { ReverseDirection = true };
        using var reader = new EventLogReader(query) { BatchSize = 16 };
        using var cancellation = token.Register(() => { try { reader.CancelReading(); } catch (EventLogException) { } });
        var text = new StringBuilder("Журнал событий Windows — последние записи файла\n\n");
        var count = 0;
        var truncated = false;
        while (count <= MaxEvents)
        {
            token.ThrowIfCancellationRequested();
            using var record = reader.ReadEvent(TimeSpan.FromMilliseconds(250));
            if (record is null) break;
            if (count++ == MaxEvents) { truncated = true; break; }
            text.AppendLine($"{record.TimeCreated:yyyy-MM-dd HH:mm:ss}  ID {record.Id}  {record.ProviderName}");
            text.AppendLine($"Уровень: {record.Level}  Компьютер: {record.MachineName}  Запись: {record.RecordId}");
            // Do not load provider message DLLs or contact remote machines to format descriptions.
            var properties = record.Properties;
            for (var index = 0; index < Math.Min(properties.Count, 12); index++)
            {
                var value = properties[index].Value;
                text.AppendLine($"  Данные {index + 1}: {Clip(value is byte[] bytes ? Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64))) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", 1000)}");
            }
            text.AppendLine();
        }
        if (count == 0) text.AppendLine("Журнал пуст.");
        if (truncated) text.AppendLine($"… показаны последние {MaxEvents} событий");
        return new(text.ToString(), $"EVTX • {Math.Min(count, MaxEvents)} событий • только чтение", truncated);
    }

    private static bool IsPcap(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 &&
        (BinaryPrimitives.ReadUInt32LittleEndian(bytes) is 0xA1B2C3D4 or 0xD4C3B2A1 or 0xA1B23C4D or 0x4D3CB2A1);
    private static bool IsPcapNg(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x0A0D0D0A;

    private static TechnicalPreviewResult LoadPcap(Stream stream, CancellationToken token)
    {
        var header = ReadBytes(stream, 24);
        if (!IsPcap(header)) throw new InvalidDataException("Неизвестная сигнатура PCAP.");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var bigEndian = magic is 0xD4C3B2A1 or 0x4D3CB2A1;
        var nanos = magic is 0xA1B23C4D or 0x4D3CB2A1;
        var link = U32(header, 20, bigEndian) & 0xFFFF;
        var text = new StringBuilder($"Захват PCAP {U16(header, 4, bigEndian)}.{U16(header, 6, bigEndian)}\n");
        text.AppendLine($"Порядок байт: {(bigEndian ? "big endian" : "little endian")}  •  Точность: {(nanos ? "наносекунды" : "микросекунды")}");
        text.AppendLine($"Канал: {LinkName(link)}  •  SnapLen: {U32(header, 16, bigEndian)}\n");
        var count = 0;
        while (stream.Position < stream.Length && count < MaxPackets)
        {
            token.ThrowIfCancellationRequested();
            var packet = ReadBytes(stream, 16);
            var captured = U32(packet, 8, bigEndian);
            var original = U32(packet, 12, bigEndian);
            if (captured > stream.Length - stream.Position || captured > 16 * 1024 * 1024)
                throw new InvalidDataException("Длина пакета выходит за пределы файла.");
            var seconds = U32(packet, 0, bigEndian);
            var fraction = U32(packet, 4, bigEndian);
            var data = ReadBytes(stream, (int)Math.Min(captured, 128));
            stream.Seek(captured - data.Length, SeekOrigin.Current);
            text.AppendLine($"{++count,4}  {CaptureTime(seconds, fraction, nanos ? 1_000_000_000d : 1_000_000d)}  {captured}/{original} байт  {PacketDescription(data, link)}");
        }
        var truncated = stream.Position < stream.Length;
        if (count == 0) text.AppendLine("Пакетов нет.");
        if (truncated) text.AppendLine($"\n… показаны первые {MaxPackets} пакетов");
        return new(text.ToString(), $"PCAP • {count} пакетов • без сетевых запросов", truncated);
    }

    private static TechnicalPreviewResult LoadPcapNg(Stream stream, CancellationToken token)
    {
        var text = new StringBuilder("Захват PCAPNG\n\n");
        var interfaces = new List<CaptureInterface>();
        var bigEndian = false;
        var count = 0;
        var blocks = 0;
        while (stream.Position < stream.Length && count < MaxPackets && blocks++ < 2048)
        {
            token.ThrowIfCancellationRequested();
            var start = stream.Position;
            var header = ReadBytes(stream, 12);
            var isSection = BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x0A0D0D0A;
            if (isSection)
            {
                var bom = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                if (bom is not (0x1A2B3C4D or 0x4D3C2B1A)) throw new InvalidDataException("Некорректный порядок байт PCAPNG.");
                bigEndian = bom == 0x4D3C2B1A;
                interfaces.Clear();
            }
            var type = U32(header, 0, bigEndian);
            var length = U32(header, 4, bigEndian);
            if (length < 12 || length % 4 != 0 || length > stream.Length - start)
                throw new InvalidDataException("Некорректная длина блока PCAPNG.");
            var payloadBytes = (int)Math.Min(length - 12, 16 * 1024);
            stream.Position = start + 8;
            var payload = ReadBytes(stream, payloadBytes);
            stream.Position = start + length - 4;
            var trailer = ReadBytes(stream, 4);
            if (U32(trailer, 0, bigEndian) != length) throw new InvalidDataException("Не совпадают длины блока PCAPNG.");
            if (isSection)
            {
                if (length < 28) throw new InvalidDataException("Слишком короткий заголовок секции PCAPNG.");
                text.AppendLine($"Секция {U16(payload, 4, bigEndian)}.{U16(payload, 6, bigEndian)}  •  {(bigEndian ? "big endian" : "little endian")}");
            }
            else if (type == 1)
            {
                if (payload.Length < 8) throw new InvalidDataException("Слишком короткий интерфейс PCAPNG.");
                var link = U16(payload, 0, bigEndian);
                var resolution = 1_000_000d;
                var offsetSeconds = 0L;
                for (var offset = 8; offset + 4 <= payload.Length;)
                {
                    var option = U16(payload, offset, bigEndian);
                    var size = U16(payload, offset + 2, bigEndian);
                    offset += 4;
                    if (option == 0) break;
                    if (offset + size > payload.Length) break;
                    if (option == 9 && size == 1)
                    {
                        var value = payload[offset];
                        resolution = (value & 0x80) == 0 ? Math.Pow(10, value) : Math.Pow(2, value & 0x7F);
                    }
                    if (option == 14 && size == 8) offsetSeconds = unchecked((long)U64(payload, offset, bigEndian));
                    offset += (size + 3) & ~3;
                }
                if (interfaces.Count >= 256) throw new InvalidDataException("Слишком много интерфейсов PCAPNG.");
                interfaces.Add(new(link, resolution, offsetSeconds));
                text.AppendLine($"Интерфейс {interfaces.Count - 1}: {LinkName(link)}  •  SnapLen: {U32(payload, 4, bigEndian)}");
            }
            else if (type == 6)
            {
                if (payload.Length < 20) throw new InvalidDataException("Слишком короткий пакет PCAPNG.");
                var interfaceId = U32(payload, 0, bigEndian);
                if (interfaceId >= interfaces.Count) throw new InvalidDataException("Пакет ссылается на отсутствующий интерфейс.");
                var info = interfaces[(int)interfaceId];
                var stamp = ((ulong)U32(payload, 4, bigEndian) << 32) | U32(payload, 8, bigEndian);
                var captured = U32(payload, 12, bigEndian);
                if ((ulong)captured > length - 32) throw new InvalidDataException("Длина пакета PCAPNG выходит за пределы блока.");
                var available = Math.Min((int)Math.Min(captured, 128), payload.Length - 20);
                text.AppendLine($"{++count,4}  {CaptureTime(stamp / info.Resolution + info.OffsetSeconds)}  {captured}/{U32(payload, 16, bigEndian)} байт  {PacketDescription(payload.AsSpan(20, available), info.Link)}");
            }
            else if (type == 3)
            {
                if (payload.Length < 4 || interfaces.Count == 0) throw new InvalidDataException("Некорректный простой пакет PCAPNG.");
                var original = U32(payload, 0, bigEndian);
                var captured = Math.Min(original, length - 16);
                var available = Math.Min((int)Math.Min(captured, 128), payload.Length - 4);
                text.AppendLine($"{++count,4}  без отметки времени  {captured}/{original} байт  {PacketDescription(payload.AsSpan(4, available), interfaces[0].Link)}");
            }
        }
        var truncated = stream.Position < stream.Length;
        if (count == 0) text.AppendLine("\nПакетов в прочитанных блоках нет.");
        if (truncated) text.AppendLine("\n… предпросмотр ограничен первыми 128 пакетами / 2048 блоками");
        return new(text.ToString(), $"PCAPNG • {count} пакетов • без сетевых запросов", truncated);
    }

    private static string PacketDescription(ReadOnlySpan<byte> packet, uint link)
    {
        var offset = 0;
        ushort etherType;
        if (link == 1 && packet.Length >= 14)
        {
            etherType = BinaryPrimitives.ReadUInt16BigEndian(packet[12..]);
            offset = 14;
            if ((etherType is 0x8100 or 0x88A8) && packet.Length >= 18)
            { etherType = BinaryPrimitives.ReadUInt16BigEndian(packet[16..]); offset = 18; }
            if (etherType == 0x0806) return "ARP";
        }
        else if (link is 101 or 228 or 229) etherType = packet.Length > 0 && packet[0] >> 4 == 6 ? (ushort)0x86DD : (ushort)0x0800;
        else return LinkName(link);
        if (etherType == 0x0800 && packet.Length >= offset + 20 && packet[offset] >> 4 == 4)
        {
            var ipHeaderLength = (packet[offset] & 0x0F) * 4;
            if (ipHeaderLength < 20 || packet.Length < offset + ipHeaderLength) return "IPv4 (неполный заголовок)";
            var source = new IPAddress(packet.Slice(offset + 12, 4));
            var destination = new IPAddress(packet.Slice(offset + 16, 4));
            var protocol = packet[offset + 9];
            var transport = offset + ipHeaderLength;
            var fragment = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 6)..]) & 0x1FFF;
            var ports = fragment == 0 && (protocol is 6 or 17) && packet.Length >= transport + 4
                ? $":{BinaryPrimitives.ReadUInt16BigEndian(packet[transport..])} → {destination}:{BinaryPrimitives.ReadUInt16BigEndian(packet[(transport + 2)..])}"
                : $" → {destination}";
            return $"{ProtocolName(protocol)} {source}{ports}";
        }
        if (etherType == 0x86DD && packet.Length >= offset + 40)
        {
            var source = new IPAddress(packet.Slice(offset + 8, 16));
            var destination = new IPAddress(packet.Slice(offset + 24, 16));
            return $"{ProtocolName(packet[offset + 6])} IPv6 {source} → {destination}";
        }
        return etherType == 0 ? LinkName(link) : $"Ethernet type 0x{etherType:X4}";
    }

    private static TechnicalPreviewResult LoadPortableExecutable(string path, Stream stream, CancellationToken token)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 64) throw new InvalidDataException("Неполный заголовок DOS.");
        stream.Position = 0x3C;
        var peOffset = reader.ReadUInt32();
        if (peOffset > stream.Length - 24 || peOffset > 16 * 1024 * 1024) throw new InvalidDataException("Некорректное смещение PE.");
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Сигнатура PE отсутствует.");
        var machine = reader.ReadUInt16();
        var sectionCount = reader.ReadUInt16();
        var timestamp = reader.ReadUInt32();
        reader.ReadUInt64();
        var optionalSize = reader.ReadUInt16();
        var characteristics = reader.ReadUInt16();
        if (sectionCount > 96 || optionalSize > 4096) throw new InvalidDataException("Слишком большой заголовок PE.");
        var optional = ReadBytes(stream, optionalSize);
        if (optional.Length < 72) throw new InvalidDataException("Неполный Optional Header PE.");
        var magic = U16(optional, 0, false);
        if (magic is not (0x10B or 0x20B)) throw new InvalidDataException("Неизвестный тип PE.");
        var text = new StringBuilder($"Исполняемый образ PE — анализ без запуска\n\n");
        text.AppendLine($"Архитектура: {machine switch { 0x14C => "x86", 0x8664 => "x64", 0xAA64 => "ARM64", 0x1C4 => "ARM", _ => $"0x{machine:X4}" }}  •  {(magic == 0x20B ? "PE32+" : "PE32")}");
        text.AppendLine($"Тип: {((characteristics & 0x2000) != 0 ? "DLL" : "приложение / драйвер")}  •  Subsystem: {U16(optional, 68, false)}");
        text.AppendLine($"Entry point RVA: 0x{U32(optional, 16, false):X8}  •  Image size: {U32(optional, 56, false):N0} байт");
        text.AppendLine($"Отметка сборки COFF: 0x{timestamp:X8} (может не быть календарным временем)\n");
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(version.FileDescription)) text.AppendLine($"Описание: {Clip(version.FileDescription, 500)}");
            if (!string.IsNullOrWhiteSpace(version.CompanyName)) text.AppendLine($"Компания: {Clip(version.CompanyName, 500)}");
            if (!string.IsNullOrWhiteSpace(version.FileVersion)) text.AppendLine($"Версия: {Clip(version.FileVersion, 100)}");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or ArgumentException) { }
        var sections = new List<PeSection>();
        text.AppendLine("\nСекции\nИмя       RVA         В файле     Размер      Флаги");
        for (var index = 0; index < sectionCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var bytes = ReadBytes(stream, 40);
            var name = Encoding.ASCII.GetString(bytes, 0, 8).TrimEnd('\0');
            var section = new PeSection(U32(bytes, 12, false), U32(bytes, 8, false), U32(bytes, 20, false), U32(bytes, 16, false));
            sections.Add(section);
            text.AppendLine($"{name,-8}  {section.Rva:X8}    {section.RawOffset:X8}    {section.RawSize,10}  0x{U32(bytes, 36, false):X8}");
        }
        var directoryOffset = magic == 0x20B ? 112 : 96;
        if (optional.Length >= directoryOffset + 16)
        {
            var importRva = U32(optional, directoryOffset + 8, false);
            var importSize = U32(optional, directoryOffset + 12, false);
            var offset = PeFileOffset(importRva, sections, stream.Length);
            if (importRva != 0 && offset >= 0)
            {
                text.AppendLine("\nИмпортируемые библиотеки (имена; библиотеки не загружаются)");
                for (var index = 0; index < Math.Min(importSize / 20, 128); index++)
                {
                    token.ThrowIfCancellationRequested();
                    stream.Position = offset + index * 20;
                    var descriptor = ReadBytes(stream, 20);
                    if (descriptor.All(value => value == 0)) break;
                    var nameOffset = PeFileOffset(U32(descriptor, 12, false), sections, stream.Length);
                    if (nameOffset < 0) break;
                    stream.Position = nameOffset;
                    var nameBytes = ReadBytes(stream, (int)Math.Min(260, stream.Length - nameOffset));
                    var terminator = Array.IndexOf(nameBytes, (byte)0);
                    text.AppendLine(Encoding.ASCII.GetString(nameBytes, 0, terminator < 0 ? nameBytes.Length : terminator));
                }
            }
        }
        return new(text.ToString(), "PE • архитектура, версии, секции и зависимости • без запуска");
    }

    private static long PeFileOffset(uint rva, IEnumerable<PeSection> sections, long length)
    {
        foreach (var section in sections)
        {
            if ((ulong)rva < section.Rva || (ulong)rva >= (ulong)section.Rva + Math.Max(section.VirtualSize, section.RawSize)) continue;
            var relative = (ulong)rva - section.Rva;
            var offset = (ulong)section.RawOffset + relative;
            return relative < section.RawSize && offset < (ulong)length ? (long)offset : -1;
        }
        return -1;
    }

    private static TechnicalPreviewResult? LoadIso(Stream stream, CancellationToken token)
    {
        const int descriptorSize = 2048;
        byte[]? primary = null;
        byte[]? joliet = null;
        for (var sector = 16; sector < 272 && (long)(sector + 1) * descriptorSize <= stream.Length; sector++)
        {
            token.ThrowIfCancellationRequested();
            stream.Position = (long)sector * descriptorSize;
            var descriptor = ReadBytes(stream, descriptorSize);
            if (!descriptor.AsSpan(1, 5).SequenceEqual(Encoding.ASCII.GetBytes("CD001")) || descriptor[6] != 1)
                return null;
            if (descriptor[0] == 1) primary = descriptor;
            if (descriptor[0] == 2 && descriptor[88] == '%' && descriptor[89] == '/' && descriptor[90] is (byte)'@' or (byte)'C' or (byte)'E')
                joliet = descriptor;
            if (descriptor[0] == 255) break;
        }
        if (primary is null && joliet is null) return null;
        var selected = joliet ?? primary!;
        var unicode = joliet is not null;
        var blockSize = U16(selected, 128, false);
        if (blockSize < 512 || blockSize > 32768) throw new InvalidDataException("Некорректный размер логического блока ISO.");
        var rootRecord = selected.AsSpan(156, 34);
        if (rootRecord[0] < 34) throw new InvalidDataException("Некорректная корневая запись ISO.");
        var rootExtent = BinaryPrimitives.ReadUInt32LittleEndian(rootRecord[2..]);
        var rootLength = BinaryPrimitives.ReadUInt32LittleEndian(rootRecord[10..]);
        var rootOffset = (long)rootExtent * blockSize;
        if (rootOffset > stream.Length || rootLength > stream.Length - rootOffset)
            throw new InvalidDataException("Корневой каталог ISO выходит за пределы файла.");
        var label = (unicode ? Encoding.BigEndianUnicode : Encoding.ASCII).GetString(selected, 40, 32).TrimEnd('\0', ' ');
        var volumeBytes = (ulong)U32(selected, 80, false) * blockSize;
        var text = new StringBuilder($"Образ {(unicode ? "Joliet / ISO9660" : "ISO9660")} — без монтирования\n\n");
        text.AppendLine($"Метка тома: {label}\nРазмер тома: {volumeBytes:N0} байт\nЛогический блок: {blockSize} байт\n\nКорневой каталог\n");
        stream.Position = rootOffset;
        var directory = ReadBytes(stream, (int)Math.Min(rootLength, 2 * 1024 * 1024));
        var truncated = rootLength > directory.Length;
        var count = 0;
        for (var offset = 0; offset < directory.Length;)
        {
            token.ThrowIfCancellationRequested();
            var recordLength = directory[offset];
            if (recordLength == 0) { offset = (offset / blockSize + 1) * blockSize; continue; }
            if (recordLength < 34 || offset + recordLength > directory.Length)
            {
                if (truncated) break;
                throw new InvalidDataException("Обрезанная запись каталога ISO.");
            }
            var nameLength = directory[offset + 32];
            if (33 + nameLength > recordLength) throw new InvalidDataException("Имя файла ISO выходит за пределы записи.");
            var nameBytes = directory.AsSpan(offset + 33, nameLength);
            if (!(nameLength == 1 && nameBytes[0] is 0 or 1))
            {
                if (count++ == 256) { truncated = true; count--; break; }
                var name = (unicode ? Encoding.BigEndianUnicode : Encoding.ASCII).GetString(nameBytes);
                var version = name.LastIndexOf(';');
                if (version >= 0 && name.AsSpan(version + 1).ToString().All(char.IsDigit)) name = name[..version];
                name = name.TrimEnd('.').Replace("\r", "\\r").Replace("\n", "\\n");
                var flags = directory[offset + 25];
                var size = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 10));
                text.AppendLine((flags & 2) != 0 ? $"[папка] {name}/" : $"[файл]  {name}   •   {size:N0} байт" + ((flags & 0x80) != 0 ? " (составная запись)" : ""));
            }
            offset += recordLength;
        }
        if (count == 0) text.AppendLine("Корневой каталог пуст.");
        if (truncated) text.AppendLine("\n… показаны первые 256 записей / 2 МиБ каталога");
        return new(text.ToString(), $"{(unicode ? "Joliet / ISO9660" : "ISO9660")} • корневой каталог • без монтирования", truncated);
    }

    private static TechnicalPreviewResult LoadCertificates(Stream stream, CancellationToken token)
    {
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Контейнер сертификатов больше 4 МиБ.");
        stream.Position = 0;
        var bytes = ReadBytes(stream, (int)stream.Length);
        var certificates = new X509Certificate2Collection();
        var text = new StringBuilder("Сертификаты X.509 — открытые метаданные\n\n");
        var truncated = false;
        try
        {
            var pem = Encoding.ASCII.GetString(bytes);
            if (pem.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                var remaining = pem.AsSpan();
                while (PemEncoding.TryFind(remaining, out var fields))
                {
                    token.ThrowIfCancellationRequested();
                    if (remaining[fields.Label].SequenceEqual("CERTIFICATE".AsSpan()))
                    {
                        if (certificates.Count == 16) { truncated = true; break; }
                        certificates.Add(new X509Certificate2(Convert.FromBase64String(remaining[fields.Base64Data].ToString())));
                    }
                    var end = fields.Location.GetOffsetAndLength(remaining.Length);
                    remaining = remaining[(end.Offset + end.Length)..];
                }
                if (certificates.Count == 0)
                    return new("PEM-контейнер не содержит открытых сертификатов X.509.\n\nЗакрытые ключи и их содержимое в предпросмотре не показываются.", "PEM • открытых сертификатов нет");
            }
            else
            {
                try { certificates.Import(bytes, (string?)null, X509KeyStorageFlags.EphemeralKeySet); }
                catch (CryptographicException)
                {
                    if (X509Certificate2.GetCertContentType(bytes) == X509ContentType.Pfx)
                        return new("Контейнер PKCS#12 / PFX защищён паролем или недоступен для чтения без пароля.\n\nОткройте файл в приложении для управления сертификатами. Пароль и хранилище ключей не изменяются.", "PKCS#12 / PFX • требуется пароль");
                    throw;
                }
            }
            foreach (var certificate in certificates.Cast<X509Certificate2>().Take(16))
            {
                token.ThrowIfCancellationRequested();
                text.AppendLine($"Владелец: {Clip(certificate.Subject, 2000)}");
                text.AppendLine($"Издатель: {Clip(certificate.Issuer, 2000)}");
                text.AppendLine($"Срок: {certificate.NotBefore:yyyy-MM-dd HH:mm:ss} — {certificate.NotAfter:yyyy-MM-dd HH:mm:ss}");
                text.AppendLine($"Серийный номер: {certificate.SerialNumber}");
                text.AppendLine($"SHA-256: {certificate.GetCertHashString(HashAlgorithmName.SHA256)}");
                text.AppendLine($"Открытый ключ: {certificate.PublicKey.Oid.FriendlyName} ({certificate.PublicKey.Oid.Value})");
                text.AppendLine($"Подпись: {certificate.SignatureAlgorithm.FriendlyName}");
                foreach (var extension in certificate.Extensions.Cast<X509Extension>().Take(16))
                    text.AppendLine($"  {extension.Oid?.FriendlyName ?? extension.Oid?.Value}: {Clip(extension.Format(false), 2000)}");
                text.AppendLine();
            }
            if (certificates.Count > 16) truncated = true;
            text.AppendLine("Цепочка доверия и отзыв сертификата не проверяются; сетевых запросов нет.");
            if (truncated) text.AppendLine("Показаны первые 16 сертификатов.");
            return new(text.ToString(), $"X.509 • {Math.Min(certificates.Count, 16)} сертификатов • локальные метаданные", truncated);
        }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    private static TechnicalPreviewResult Hex(Stream stream, string? warning, CancellationToken token)
    {
        stream.Position = 0;
        var bytes = ReadBytes(stream, (int)Math.Min(stream.Length, MaxHexBytes));
        var text = new StringBuilder();
        if (warning is not null) text.AppendLine(warning + "\n");
        text.AppendLine($"Двоичный файл • {stream.Length:N0} байт\n");
        text.AppendLine("Смещение   00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ASCII");
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            token.ThrowIfCancellationRequested();
            text.Append($"{offset:X8}   ");
            for (var index = 0; index < 16; index++)
                text.Append(offset + index < bytes.Length ? $"{bytes[offset + index]:X2} " : "   ");
            text.Append(" ");
            for (var index = offset; index < Math.Min(offset + 16, bytes.Length); index++)
                text.Append(bytes[index] is >= 32 and <= 126 ? (char)bytes[index] : '.');
            text.AppendLine();
        }
        if (bytes.Length == 0) text.AppendLine("Файл пуст.");
        var truncated = stream.Length > bytes.Length;
        if (truncated) text.AppendLine($"\n… показаны первые {MaxHexBytes:N0} байт");
        return new(text.ToString(), "Hex • шестнадцатеричный просмотр" + (truncated ? " • первые 4 КиБ" : ""), truncated);
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    private static byte[] ReadBytes(Stream stream, int count)
    {
        var bytes = new byte[count];
        var read = 0;
        while (read < count)
        {
            var current = stream.Read(bytes, read, count - read);
            if (current == 0) throw new EndOfStreamException("Файл обрезан или изменился во время чтения.");
            read += current;
        }
        return bytes;
    }
    private static ushort U16(byte[] bytes, int offset, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset)) : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset)) : BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "…";
    private static string ProtocolName(byte protocol) => protocol switch { 1 => "ICMP", 6 => "TCP", 17 => "UDP", 58 => "ICMPv6", _ => $"IP protocol {protocol}" };
    private static string LinkName(uint link) => link switch { 1 => "Ethernet", 101 => "Raw IP", 113 => "Linux cooked capture", 127 => "IEEE 802.11 radiotap", 228 => "IPv4", 229 => "IPv6", 276 => "Linux cooked capture v2", _ => $"LINKTYPE {link}" };
    private static string CaptureTime(uint seconds, uint fraction, double resolution) => CaptureTime(seconds + fraction / resolution);
    private static string CaptureTime(double seconds)
    {
        try { return DateTimeOffset.UnixEpoch.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss.ffffff 'UTC'", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return $"timestamp={seconds.ToString("G17", CultureInfo.InvariantCulture)}"; }
    }
    private sealed record CaptureInterface(uint Link, double Resolution, long OffsetSeconds);
    private sealed record PeSection(uint Rva, uint VirtualSize, uint RawOffset, uint RawSize);

    private static class Sqlite
    {
        private const string Dll = "winsqlite3.dll";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ProgressCallback(IntPtr userData);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_limit(IntPtr db, int category, int value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void sqlite3_progress_handler(IntPtr db, int operations, ProgressCallback? callback, IntPtr data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        internal static string Error(IntPtr db) => db == IntPtr.Zero ? "Не удалось открыть SQLite." : Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "Ошибка SQLite.";
        internal static string SchemaValue(IntPtr statement, int column)
        {
            var pointer = sqlite3_column_text(statement, column);
            var count = sqlite3_column_bytes(statement, column);
            if (count > 32 * 1024) throw new InvalidDataException("Описание объекта SQLite превышает 32 КиБ.");
            return pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(pointer, count) ?? "";
        }
        internal static string Value(IntPtr statement, int column)
        {
            var type = sqlite3_column_type(statement, column);
            if (type == 5) return "NULL";
            if (type == 4) return $"<BLOB {sqlite3_column_bytes(statement, column)} байт>";
            var pointer = sqlite3_column_text(statement, column);
            var count = Math.Min(sqlite3_column_bytes(statement, column), 2048);
            return pointer == IntPtr.Zero ? "" : Clip((Marshal.PtrToStringUTF8(pointer, count) ?? "").Replace("\r", "\\r").Replace("\n", "\\n"), 500);
        }
    }
}
