namespace ECMA335Printer
{
    /// <summary>
    /// PE文件的节（Section）信息
    /// </summary>
    class Section
    {
        public string Name { get; set; } = string.Empty;
        public uint VirtualSize { get; set; }
        public uint VirtualAddress { get; set; }
        public uint SizeOfRawData { get; set; }
        public uint PointerToRawData { get; set; }
        public uint Characteristics { get; set; }
    }

    /// <summary>
    /// CLI头（.NET特有的头部信息）
    /// </summary>
    class CLIHeader
    {
        public uint Cb { get; set; }
        public ushort MajorRuntimeVersion { get; set; }
        public ushort MinorRuntimeVersion { get; set; }
        public uint MetadataRVA { get; set; }
        public uint MetadataSize { get; set; }
        public uint Flags { get; set; }
        public uint EntryPointToken { get; set; }
        public uint ResourcesRVA { get; set; }
        public uint ResourcesSize { get; set; }
        public uint StrongNameSignatureRVA { get; set; }
        public uint StrongNameSignatureSize { get; set; }
    }

    /// <summary>
    /// 元数据根信息
    /// </summary>
    class MetadataRoot
    {
        public ushort MajorVersion { get; set; }
        public ushort MinorVersion { get; set; }
        public string Version { get; set; } = string.Empty;
        public Dictionary<string, MetadataStream> Streams { get; set; } = new Dictionary<string, MetadataStream>();
        public int StringIndexSize { get; set; }
        public int GuidIndexSize { get; set; }
        public int BlobIndexSize { get; set; }
        public int[] TableRowCounts { get; set; } = Array.Empty<int>();
    }

    /// <summary>
    /// 元数据流信息
    /// </summary>
    class MetadataStream
    {
        public string Name { get; set; } = string.Empty;
        public uint Offset { get; set; }
        public uint Size { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }
}
