using System.IO;
using System.Text;

namespace QuickPreview;

internal enum PreviewKind
{
    Unknown, Folder, Image, Audio, Video, Pdf, Archive, Office, Text, Technical,
    Model3D, Font, Email, Ebook
}

internal static class FormatCatalog
{
    public static readonly HashSet<string> ImageExtensions = Set(
        ".png .jpg .jpeg .jpe .jfif .bmp .dib .gif .tif .tiff .ico .wdp .webp .avif .heic .heif .svg .svgz " +
        ".psd .psb .tga .dds .exr .hdr .jxl .jp2 .j2k .pcx .pbm .pgm .ppm .pnm .qoi .dng .cr2 .nef .arw .raf .orf .rw2 .xcf .kra .ora");
    public static readonly HashSet<string> AudioExtensions = Set(
        ".mp3 .wav .wma .aac .m4a .flac .ogg .oga .opus .aiff .aif .aifc .mid .midi");
    public static readonly HashSet<string> VideoExtensions = Set(
        ".mp4 .m4v .mov .avi .wmv .webm .mkv .mpg .mpeg .m2v .mts .m2ts .ts .3gp .3g2 .ogv");
    public static readonly HashSet<string> TextExtensions = Set(
        ".txt .text .md .markdown .rst .adoc .asciidoc .org .tex .bib .log .out .err " +
        ".json .jsonl .ndjson .har .ipynb .xml .xsd .xsl .xslt .xaml .resx .wsdl .yaml .yml .csv .tsv " +
        ".ini .cfg .conf .config .toml .env .properties .editorconfig .gitconfig .gitignore .gitattributes .gitmodules " +
        ".npmrc .yarnrc .nvmrc .babelrc .eslintrc .prettierrc .lock .dockerignore .dockerfile " +
        ".cs .csx .fs .fsx .vb .cpp .cc .cxx .c .h .hh .hpp .hxx .m .mm .java .kt .kts .swift " +
        ".js .mjs .cjs .jsx .ts .mts .cts .tsx .html .htm .xhtml .css .scss .sass .less .styl " +
        ".py .pyw .pyi .rb .rake .gemspec .php .go .rs .r .jl .lua .pl .pm .t .dart .gd .gdshader " +
        ".ex .exs .erl .hrl .clj .cljs .cljc .edn .scala .sc .groovy .gradle .sql .graphql .gql " +
        ".vue .svelte .astro .sh .bash .zsh .fish .ps1 .psm1 .psd1 .bat .cmd .reg .vbs .ahk .au3 " +
        ".sln .slnx .csproj .fsproj .vbproj .vcxproj .props .targets .nuspec .iml .cmake .mk .mak " +
        ".tf .tfvars .hcl .service .socket .timer .desktop .rules .spec .manifest .s .asm .diff .patch " +
        ".shader .glsl .hlsl .vert .frag .geom .comp .wgsl .srt .vtt .ass .sub .nfo .diz .cue .m3u .m3u8 .pls .url .ics .vcf " +
        ".gcode .gco .nc .kicad_pcb .kicad_sch .kicad_mod .kicad_sym .kicad_wks .asc .ascx .ascad");
    public static readonly HashSet<string> TechnicalExtensions = Set(
        ".db .db3 .sqlite .sqlite3 .evtx .pcap .pcapng .cap .iso .img .exe .dll .sys .ocx .scr .cpl .drv .acm " +
        ".cer .crt .der .pem .pfx .p12 .p7b .p7c");

    private static readonly HashSet<string> TextNames = Set(
        "README LICENSE COPYING NOTICE AUTHORS CHANGELOG CHANGES TODO Dockerfile Containerfile Makefile GNUmakefile " +
        "Gemfile Rakefile Procfile Vagrantfile Jenkinsfile Brewfile Justfile .bashrc .bash_profile .zshrc .profile " +
        "known_hosts authorized_keys hosts fstab passwd shadow group resolv.conf");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsKnownText(string path)
    {
        var name = Path.GetFileName(path);
        return TextExtensions.Contains(Path.GetExtension(path)) || TextNames.Contains(name) ||
               name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Containerfile.", StringComparison.OrdinalIgnoreCase);
    }

    public static PreviewKind Classify(string path)
    {
        if (Directory.Exists(path)) return PreviewKind.Folder;
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return PreviewKind.Pdf;
        if (TechnicalExtensions.Contains(extension)) return PreviewKind.Technical;
        if (ImageExtensions.Contains(extension)) return PreviewKind.Image;
        // TypeScript is source code; MPEG transport streams are recognized by sniffing in media code.
        if (IsKnownText(path)) return PreviewKind.Text;
        if (AudioExtensions.Contains(extension)) return PreviewKind.Audio;
        if (VideoExtensions.Contains(extension)) return PreviewKind.Video;
        return PreviewKind.Unknown;
    }

    /// <summary>Returns a Unicode encoding only when the sample is valid readable text.</summary>
    public static Encoding? DetectTextEncoding(ReadOnlySpan<byte> bytes, bool endOfFile = false)
    {
        if (bytes.IsEmpty) return StrictUtf8;
        var encoding = BomEncoding(bytes, out var skip) ?? InferUnicodeEncoding(bytes) ?? StrictUtf8;
        try
        {
            var decoder = encoding.GetDecoder();
            var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
            decoder.Convert(bytes[skip..], chars, endOfFile, out _, out var charsUsed, out _);
            var controls = 0;
            for (var index = 0; index < charsUsed; index++)
            {
                var character = chars[index];
                if (character == '\0') return null;
                if (char.IsControl(character) && character is not ('\r' or '\n' or '\t' or '\f')) controls++;
            }
            return controls == 0 || (charsUsed >= 128 && controls <= charsUsed / 100) ? encoding : null;
        }
        catch (DecoderFallbackException) { return null; }
    }

    public static bool LooksLikeText(ReadOnlySpan<byte> bytes, bool endOfFile = false) =>
        DetectTextEncoding(bytes, endOfFile) is not null;

    public static string DecodeText(byte[] bytes)
    {
        var encoding = BomEncoding(bytes, out var skip) ?? InferUnicodeEncoding(bytes);
        if (encoding is not null)
        {
            // A capped preview may end in the middle of a Unicode code point.
            var decoder = encoding.GetDecoder();
            var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
            decoder.Convert(bytes.AsSpan(skip), chars, false, out _, out var charsUsed, out _);
            return new string(chars, 0, charsUsed);
        }

        try
        {
            var decoder = StrictUtf8.GetDecoder();
            var chars = new char[bytes.Length];
            decoder.Convert(bytes, chars, false, out _, out var charsUsed, out _);
            return new string(chars, 0, charsUsed);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static Encoding? BomEncoding(ReadOnlySpan<byte> bytes, out int skip)
    {
        skip = 0;
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { skip = 4; return new UTF32Encoding(false, false, true); }
        if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { skip = 4; return new UTF32Encoding(true, false, true); }
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { skip = 3; return StrictUtf8; }
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) { skip = 2; return new UnicodeEncoding(false, false, true); }
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) { skip = 2; return new UnicodeEncoding(true, false, true); }
        return null;
    }

    private static Encoding? InferUnicodeEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return null;
        var sample = bytes[..Math.Min(bytes.Length, 4096)];
        var groups = sample.Length / 4;
        Span<int> zeros = stackalloc int[4];
        zeros.Clear();
        for (var index = 0; index < groups * 4; index++)
            if (sample[index] == 0) zeros[index % 4]++;
        if (groups >= 2 && zeros[1] > groups * 0.6 && zeros[2] > groups * 0.6 && zeros[3] > groups * 0.6 && zeros[0] < groups * 0.1)
            return new UTF32Encoding(false, false, true);
        if (groups >= 2 && zeros[0] > groups * 0.6 && zeros[1] > groups * 0.6 && zeros[2] > groups * 0.6 && zeros[3] < groups * 0.1)
            return new UTF32Encoding(true, false, true);
        var pairs = sample.Length / 2;
        var even = zeros[0] + zeros[2];
        var odd = zeros[1] + zeros[3];
        if (odd > pairs * 0.3 && even < pairs * 0.05) return new UnicodeEncoding(false, false, true);
        if (even > pairs * 0.3 && odd < pairs * 0.05) return new UnicodeEncoding(true, false, true);
        return null;
    }

    private static HashSet<string> Set(string values) =>
        new(values.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
}
