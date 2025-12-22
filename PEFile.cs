using System.Text;

namespace ECMA335Printer
{
    /// <summary>
    /// PE文件解析器和打印器
    /// </summary>
    class PEFile
    {
        private readonly string _filePath;
        private byte[]? _fileData;
        private BinaryReader? _reader;

        // PE Headers
        private int _peHeaderOffset;
        private int _numberOfSections;
        private int _optionalHeaderSize;
        
        // Optional Header
        private ushort _magic;
        private uint _imageBase;
        private int _fileAlignment;
        private int _sectionAlignment;
        
        // Sections
        private List<Section> _sections = new List<Section>();
        
        // CLI Header
        private CLIHeader? _cliHeader;
        
        // Metadata
        private MetadataRoot? _metadata;

        public PEFile(string filePath)
        {
            _filePath = filePath;
        }

        public void Parse()
        {
            _fileData = File.ReadAllBytes(_filePath);
            _reader = new BinaryReader(new MemoryStream(_fileData));

            ParseDOSHeader();
            ParsePEHeader();
            ParseOptionalHeader();
            ParseSections();
            ParseCLIHeader();
            ParseMetadata();
        }

        private void ParseDOSHeader()
        {
            // DOS Header starts at offset 0
            _reader!.BaseStream.Seek(0, SeekOrigin.Begin);
            
            // Check DOS signature "MZ"
            ushort dosSignature = _reader.ReadUInt16();
            if (dosSignature != 0x5A4D) // "MZ"
            {
                throw new Exception("Invalid DOS signature");
            }

            // PE header offset is at 0x3C
            _reader!.BaseStream.Seek(0x3C, SeekOrigin.Begin);
            _peHeaderOffset = _reader.ReadInt32();
        }

        private void ParsePEHeader()
        {
            _reader!.BaseStream.Seek(_peHeaderOffset, SeekOrigin.Begin);
            
            // Check PE signature "PE\0\0"
            uint peSignature = _reader.ReadUInt32();
            if (peSignature != 0x00004550) // "PE\0\0"
            {
                throw new Exception("Invalid PE signature");
            }

            // COFF Header
            ushort machine = _reader.ReadUInt16();
            _numberOfSections = _reader.ReadUInt16();
            uint timeDateStamp = _reader.ReadUInt32();
            uint pointerToSymbolTable = _reader.ReadUInt32();
            uint numberOfSymbols = _reader.ReadUInt32();
            _optionalHeaderSize = _reader.ReadUInt16();
            ushort characteristics = _reader.ReadUInt16();
        }

        private void ParseOptionalHeader()
        {
            long optionalHeaderStart = _reader!.BaseStream.Position;
            
            _magic = _reader.ReadUInt16();
            bool is64Bit = _magic == 0x20B;

            byte majorLinkerVersion = _reader.ReadByte();
            byte minorLinkerVersion = _reader.ReadByte();
            uint sizeOfCode = _reader.ReadUInt32();
            uint sizeOfInitializedData = _reader.ReadUInt32();
            uint sizeOfUninitializedData = _reader.ReadUInt32();
            uint addressOfEntryPoint = _reader.ReadUInt32();
            uint baseOfCode = _reader.ReadUInt32();

            if (!is64Bit)
            {
                uint baseOfData = _reader.ReadUInt32();
                _imageBase = _reader.ReadUInt32();
            }
            else
            {
                _imageBase = (uint)_reader.ReadUInt64();
            }

            _sectionAlignment = _reader.ReadInt32();
            _fileAlignment = _reader.ReadInt32();

            // Skip to data directories
            _reader!.BaseStream.Seek(optionalHeaderStart + _optionalHeaderSize, SeekOrigin.Begin);
        }

        private void ParseSections()
        {
            for (int i = 0; i < _numberOfSections; i++)
            {
                var section = new Section();
                section.Name = Encoding.ASCII.GetString(_reader!.ReadBytes(8)).TrimEnd('\0');
                section.VirtualSize = _reader.ReadUInt32();
                section.VirtualAddress = _reader.ReadUInt32();
                section.SizeOfRawData = _reader.ReadUInt32();
                section.PointerToRawData = _reader.ReadUInt32();
                _reader.ReadUInt32(); // PointerToRelocations
                _reader.ReadUInt32(); // PointerToLinenumbers
                _reader.ReadUInt16(); // NumberOfRelocations
                _reader.ReadUInt16(); // NumberOfLinenumbers
                section.Characteristics = _reader.ReadUInt32();
                
                _sections.Add(section);
            }
        }

        private void ParseCLIHeader()
        {
            // Find .text section (usually contains CLI header)
            var textSection = _sections.FirstOrDefault(s => s.Name == ".text");
            if (textSection == null)
            {
                throw new Exception(".text section not found");
            }

            // Go back to optional header to read data directories
            _reader!.BaseStream.Seek(_peHeaderOffset + 24 + (_magic == 0x20B ? 112 : 96), SeekOrigin.Begin);
            
            // Skip to data directory 14 (CLI Header)
            _reader.BaseStream.Seek(14 * 8, SeekOrigin.Current);
            uint cliHeaderRVA = _reader.ReadUInt32();
            uint cliHeaderSize = _reader.ReadUInt32();

            if (cliHeaderRVA == 0)
            {
                throw new Exception("Not a .NET assembly");
            }

            // Convert RVA to file offset
            uint cliHeaderOffset = RVAToFileOffset(cliHeaderRVA);
            _reader!.BaseStream.Seek(cliHeaderOffset, SeekOrigin.Begin);

            _cliHeader = new CLIHeader();
            _cliHeader.Cb = _reader.ReadUInt32();
            _cliHeader.MajorRuntimeVersion = _reader.ReadUInt16();
            _cliHeader.MinorRuntimeVersion = _reader.ReadUInt16();
            _cliHeader.MetadataRVA = _reader.ReadUInt32();
            _cliHeader.MetadataSize = _reader.ReadUInt32();
            _cliHeader.Flags = _reader.ReadUInt32();
            _cliHeader.EntryPointToken = _reader.ReadUInt32();
            _cliHeader.ResourcesRVA = _reader.ReadUInt32();
            _cliHeader.ResourcesSize = _reader.ReadUInt32();
            _cliHeader.StrongNameSignatureRVA = _reader.ReadUInt32();
            _cliHeader.StrongNameSignatureSize = _reader.ReadUInt32();
        }

        private void ParseMetadata()
        {
            uint metadataOffset = RVAToFileOffset(_cliHeader!.MetadataRVA);
            _reader!.BaseStream.Seek(metadataOffset, SeekOrigin.Begin);

            _metadata = new MetadataRoot();
            
            // Metadata root
            uint signature = _reader.ReadUInt32();
            if (signature != 0x424A5342) // "BSJB"
            {
                throw new Exception("Invalid metadata signature");
            }

            _metadata.MajorVersion = _reader.ReadUInt16();
            _metadata.MinorVersion = _reader.ReadUInt16();
            _reader.ReadUInt32(); // Reserved
            uint versionLength = _reader.ReadUInt32();
            _metadata.Version = Encoding.UTF8.GetString(_reader.ReadBytes((int)versionLength)).TrimEnd('\0');
            
            // Align to 4 bytes
            while (_reader.BaseStream.Position % 4 != 0)
                _reader.ReadByte();

            _reader.ReadUInt16(); // Flags
            ushort numberOfStreams = _reader.ReadUInt16();

            // Parse stream headers
            for (int i = 0; i < numberOfStreams; i++)
            {
                uint offset = _reader.ReadUInt32();
                uint size = _reader.ReadUInt32();
                
                StringBuilder nameBuilder = new StringBuilder();
                byte b;
                while ((b = _reader.ReadByte()) != 0)
                {
                    nameBuilder.Append((char)b);
                }
                
                // Align to 4 bytes
                while (_reader.BaseStream.Position % 4 != 0)
                    _reader.ReadByte();

                string streamName = nameBuilder.ToString();
                uint streamOffset = (uint)(metadataOffset + offset);

                _metadata.Streams[streamName] = new MetadataStream
                {
                    Name = streamName,
                    Offset = streamOffset,
                    Size = size
                };
            }

            // Parse streams
            ParseMetadataStreams();
        }

        private void ParseMetadataStreams()
        {
            // Parse #Strings stream
            if (_metadata!.Streams.ContainsKey("#Strings"))
            {
                var stream = _metadata.Streams["#Strings"];
                _reader!.BaseStream.Seek(stream.Offset, SeekOrigin.Begin);
                stream.Data = _reader.ReadBytes((int)stream.Size);
            }

            // Parse #US stream
            if (_metadata!.Streams.ContainsKey("#US"))
            {
                var stream = _metadata.Streams["#US"];
                _reader!.BaseStream.Seek(stream.Offset, SeekOrigin.Begin);
                stream.Data = _reader.ReadBytes((int)stream.Size);
            }

            // Parse #Blob stream
            if (_metadata!.Streams.ContainsKey("#Blob"))
            {
                var stream = _metadata.Streams["#Blob"];
                _reader!.BaseStream.Seek(stream.Offset, SeekOrigin.Begin);
                stream.Data = _reader.ReadBytes((int)stream.Size);
            }

            // Parse #GUID stream
            if (_metadata!.Streams.ContainsKey("#GUID"))
            {
                var stream = _metadata.Streams["#GUID"];
                _reader!.BaseStream.Seek(stream.Offset, SeekOrigin.Begin);
                stream.Data = _reader.ReadBytes((int)stream.Size);
            }

            // Parse #~ or #- stream (metadata tables)
            string tablesStreamName = _metadata!.Streams.ContainsKey("#~") ? "#~" : "#-";
            if (_metadata!.Streams.ContainsKey(tablesStreamName))
            {
                var stream = _metadata.Streams[tablesStreamName];
                _reader!.BaseStream.Seek(stream.Offset, SeekOrigin.Begin);
                
                var parser = new MetadataTablesParser(_reader, _metadata);
                parser.ParseTables();
            }
        }

        private uint RVAToFileOffset(uint rva)
        {
            foreach (var section in _sections)
            {
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.VirtualSize)
                {
                    return section.PointerToRawData + (rva - section.VirtualAddress);
                }
            }
            throw new Exception($"Cannot convert RVA 0x{rva:X} to file offset");
        }

        private string ReadString(uint offset)
        {
            if (!_metadata!.Streams.ContainsKey("#Strings"))
                return "";

            var data = _metadata.Streams["#Strings"].Data;
            if (offset >= data.Length)
                return "";

            int end = (int)offset;
            while (end < data.Length && data[end] != 0)
                end++;

            return Encoding.UTF8.GetString(data, (int)offset, end - (int)offset);
        }

        public void PrintAssemblyInfo()
        {
            var printer = new AssemblyInfoPrinter(_filePath, _fileData!, _magic, _cliHeader!, _metadata!, _sections);
            printer.Print();
        }
    }
}
