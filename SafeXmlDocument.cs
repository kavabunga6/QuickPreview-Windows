using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace QuickPreview;

/// <summary>Bounds untrusted package XML before a tree or recursive text extraction is built.</summary>
internal static class SafeXmlDocument
{
    internal const int MaxDepth = 128;

    internal static XDocument Load(Stream stream, long maxCharacters, CancellationToken token,
        LoadOptions options = LoadOptions.None, bool preserveMarkup = false)
    {
        token.ThrowIfCancellationRequested();
        using var reader = new BoundedReader(XmlReader.Create(stream, Settings(maxCharacters, preserveMarkup)), token);
        return Load(reader, token, options);
    }

    internal static XDocument Load(TextReader text, long maxCharacters, CancellationToken token,
        LoadOptions options = LoadOptions.None, bool preserveMarkup = false)
    {
        token.ThrowIfCancellationRequested();
        using var reader = new BoundedReader(XmlReader.Create(text, Settings(maxCharacters, preserveMarkup)), token);
        return Load(reader, token, options);
    }

    private static XDocument Load(XmlReader reader, CancellationToken token, LoadOptions options)
    {
        var document = XDocument.Load(reader, options);
        token.ThrowIfCancellationRequested();
        return document;
    }

    private static XmlReaderSettings Settings(long maxCharacters, bool preserveMarkup) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
        MaxCharactersInDocument = maxCharacters, IgnoreComments = !preserveMarkup,
        IgnoreProcessingInstructions = !preserveMarkup
    };

    private sealed class BoundedReader : XmlReader
    {
        private readonly XmlReader _reader;
        private readonly CancellationToken _token;
        internal BoundedReader(XmlReader reader, CancellationToken token) { _reader = reader; _token = token; }

        public override bool Read()
        {
            _token.ThrowIfCancellationRequested();
            var result = _reader.Read();
            if (result && _reader.Depth > MaxDepth)
                throw new XmlException("XML документа содержит слишком много вложенных элементов для быстрого предпросмотра.");
            return result;
        }

        public override int AttributeCount => _reader.AttributeCount;
        public override string BaseURI => _reader.BaseURI;
        public override int Depth => _reader.Depth;
        public override bool EOF => _reader.EOF;
        public override bool HasValue => _reader.HasValue;
        public override bool IsEmptyElement => _reader.IsEmptyElement;
        public override string LocalName => _reader.LocalName;
        public override string Name => _reader.Name;
        public override XmlNameTable NameTable => _reader.NameTable;
        public override string NamespaceURI => _reader.NamespaceURI;
        public override XmlNodeType NodeType => _reader.NodeType;
        public override string Prefix => _reader.Prefix;
        public override ReadState ReadState => _reader.ReadState;
        public override string Value => _reader.Value;
        public override string? GetAttribute(string name) => _reader.GetAttribute(name);
        public override string? GetAttribute(string name, string? namespaceURI) => _reader.GetAttribute(name, namespaceURI);
        public override string GetAttribute(int index) => _reader.GetAttribute(index);
        public override string? LookupNamespace(string prefix) => _reader.LookupNamespace(prefix);
        public override bool MoveToAttribute(string name) => _reader.MoveToAttribute(name);
        public override bool MoveToAttribute(string name, string? namespaceURI) => _reader.MoveToAttribute(name, namespaceURI);
        public override bool MoveToElement() => _reader.MoveToElement();
        public override bool MoveToFirstAttribute() => _reader.MoveToFirstAttribute();
        public override bool MoveToNextAttribute() => _reader.MoveToNextAttribute();
        public override bool ReadAttributeValue() => _reader.ReadAttributeValue();
        public override void ResolveEntity() => _reader.ResolveEntity();
        public override void Close() => _reader.Close();
    }
}
