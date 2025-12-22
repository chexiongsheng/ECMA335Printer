namespace ECMA335Printer
{
    /// <summary>
    /// 程序集信息打印器
    /// </summary>
    class AssemblyInfoPrinter
    {
        private readonly string _filePath;
        private readonly byte[] _fileData;
        private readonly ushort _magic;
        private readonly CLIHeader _cliHeader;
        private readonly MetadataRoot _metadata;
        private readonly List<Section> _sections;

        public AssemblyInfoPrinter(
            string filePath,
            byte[] fileData,
            ushort magic,
            CLIHeader cliHeader,
            MetadataRoot metadata,
            List<Section> sections)
        {
            _filePath = filePath;
            _fileData = fileData;
            _magic = magic;
            _cliHeader = cliHeader;
            _metadata = metadata;
            _sections = sections;
        }

        public void Print()
        {
            PrintHeader();
            PrintSizeBreakdown();
            PrintSections();
            PrintMetadataStreams();
            PrintMetadataTables();
            PrintFooter();
        }

        private void PrintHeader()
        {
            Console.WriteLine($"Assembly: {Path.GetFileName(_filePath)}");
            Console.WriteLine("=" + new string('=', 80));
            Console.WriteLine();

            Console.WriteLine($"PE Format: {(_magic == 0x20B ? "PE32+" : "PE32")}");
            Console.WriteLine($"CLI Runtime Version: {_cliHeader.MajorRuntimeVersion}.{_cliHeader.MinorRuntimeVersion}");
            Console.WriteLine($"Metadata Version: {_metadata.Version}");
            Console.WriteLine();
        }

        private void PrintSizeBreakdown()
        {
            Console.WriteLine("Assembly Size Breakdown:");
            Console.WriteLine("=" + new string('=', 80));
            
            long fileSize = _fileData.Length;
            Console.WriteLine($"Total File Size:           {fileSize:N0} bytes");
            Console.WriteLine($"Metadata Size:             {_cliHeader.MetadataSize:N0} bytes ({(_cliHeader.MetadataSize * 100.0 / fileSize):F2}%)");
            
            if (_cliHeader.ResourcesSize > 0)
                Console.WriteLine($"Resources Size:            {_cliHeader.ResourcesSize:N0} bytes ({(_cliHeader.ResourcesSize * 100.0 / fileSize):F2}%)");
            
            if (_cliHeader.StrongNameSignatureSize > 0)
                Console.WriteLine($"Strong Name Signature:     {_cliHeader.StrongNameSignatureSize:N0} bytes ({(_cliHeader.StrongNameSignatureSize * 100.0 / fileSize):F2}%)");

            Console.WriteLine();
        }

        private void PrintSections()
        {
            Console.WriteLine("Sections:");
            foreach (var section in _sections)
            {
                long fileSize = _fileData.Length;
                Console.WriteLine($"  {section.Name,-8} VirtualSize: {section.VirtualSize,10:N0}  RawSize: {section.SizeOfRawData,10:N0}  ({(section.SizeOfRawData * 100.0 / fileSize):F2}%)");
            }
            Console.WriteLine();
        }

        private void PrintMetadataStreams()
        {
            Console.WriteLine("Metadata Streams:");
            long fileSize = _fileData.Length;
            foreach (var stream in _metadata.Streams.Values)
            {
                Console.WriteLine($"  {stream.Name,-12} Size: {stream.Size,10:N0} bytes  ({(stream.Size * 100.0 / fileSize):F2}%)");
            }
            Console.WriteLine();
        }

        private void PrintMetadataTables()
        {
            Console.WriteLine("Metadata Tables:");
            string[] tableNames = MetadataTablesParser.GetTableNames();

            for (int i = 0; i < _metadata.TableRowCounts.Length && i < tableNames.Length; i++)
            {
                if (_metadata.TableRowCounts[i] > 0)
                {
                    Console.WriteLine($"  {tableNames[i],-25} {_metadata.TableRowCounts[i],6} rows");
                }
            }
            Console.WriteLine();
        }

        private void PrintFooter()
        {
            Console.WriteLine("Note: Full metadata table parsing requires implementing all table schemas.");
            Console.WriteLine("This is a simplified version showing PE structure and basic metadata info.");
        }
    }
}
