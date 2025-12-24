using System.Text;

namespace ECMA335Printer
{
    /// <summary>
    /// 元数据打印工具
    /// </summary>
    class MetadataPrinter
    {
        private readonly MetadataRoot _metadata;
        private readonly MetadataTablesParser _parser;

        public MetadataPrinter(MetadataRoot metadata, MetadataTablesParser parser)
        {
            _metadata = metadata;
            _parser = parser;
        }

        public void PrintSummary()
        {
            Console.WriteLine("\n=== Metadata Tables Summary ===\n");

            var tableNames = MetadataTablesParser.GetTableNames();
            int totalTables = 0;
            int totalRows = 0;

            for (int i = 0; i < _metadata.TableRowCounts.Length; i++)
            {
                int count = _metadata.TableRowCounts[i];
                if (count > 0 && i < tableNames.Length)
                {
                    Console.WriteLine($"Table 0x{i:X2}: {tableNames[i],-25} {count,6} rows");
                    totalTables++;
                    totalRows += count;
                }
            }

            Console.WriteLine($"\nTotal: {totalTables} tables, {totalRows} rows");
        }

        public void PrintModuleInfo()
        {
            if (_metadata.ModuleTable.Length == 0)
                return;

            Console.WriteLine("\n=== Module Information ===\n");
            var module = _metadata.ModuleTable[0];
            Console.WriteLine($"Name: {_parser.ReadString(module.Name)}");
            Console.WriteLine($"MVID: {_parser.ReadGuid(module.Mvid)}");
            Console.WriteLine($"Generation: {module.Generation}");
        }

        public void PrintAssemblyInfo()
        {
            if (_metadata.AssemblyTable.Length == 0)
                return;

            Console.WriteLine("\n=== Assembly Information ===\n");
            var assembly = _metadata.AssemblyTable[0];
            Console.WriteLine($"Name: {_parser.ReadString(assembly.Name)}");
            Console.WriteLine($"Version: {assembly.MajorVersion}.{assembly.MinorVersion}.{assembly.BuildNumber}.{assembly.RevisionNumber}");
            Console.WriteLine($"Culture: {_parser.ReadString(assembly.Culture)}");
            Console.WriteLine($"Flags: 0x{assembly.Flags:X8}");
        }

        public void PrintTypeDefSummary(int maxTypes = 10)
        {
            if (_metadata.TypeDefTable.Length == 0)
                return;

            Console.WriteLine($"\n=== TypeDef Table (showing first {Math.Min(maxTypes, _metadata.TypeDefTable.Length)} types) ===\n");

            for (int i = 0; i < Math.Min(maxTypes, _metadata.TypeDefTable.Length); i++)
            {
                var type = _metadata.TypeDefTable[i];
                string ns = _parser.ReadString(type.TypeNamespace);
                string name = _parser.ReadString(type.TypeName);
                string fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                Console.WriteLine($"  [{i + 1}] {fullName}");
            }
        }

        public void PrintMethodDefSummary(int maxMethods = 10)
        {
            if (_metadata.MethodDefTable.Length == 0)
                return;

            Console.WriteLine($"\n=== MethodDef Table (showing first {Math.Min(maxMethods, _metadata.MethodDefTable.Length)} methods) ===\n");

            for (int i = 0; i < Math.Min(maxMethods, _metadata.MethodDefTable.Length); i++)
            {
                var method = _metadata.MethodDefTable[i];
                string name = method.NameString ?? _parser.ReadString(method.Name);
                string hasBody = method.MethodBody != null ? "✓" : "✗";
                uint ilSize = method.MethodBody?.CodeSize ?? 0;

                Console.WriteLine($"  [{i + 1}] {name,-40} RVA: 0x{method.RVA:X8}  Body: {hasBody}  IL Size: {ilSize} bytes");
            }
        }

        public void PrintMethodBodyDetails(string methodName)
        {
            var method = _metadata.MethodDefTable.FirstOrDefault(m => 
                (m.NameString ?? _parser.ReadString(m.Name)) == methodName);

            if (method == null)
            {
                Console.WriteLine($"\nMethod '{methodName}' not found.");
                return;
            }

            Console.WriteLine($"\n=== Method Body Details: {methodName} ===\n");
            Console.WriteLine($"RVA: 0x{method.RVA:X8}");
            Console.WriteLine($"Flags: 0x{method.Flags:X4}");
            Console.WriteLine($"ImplFlags: 0x{method.ImplFlags:X4}");

            if (method.MethodBody != null)
            {
                var body = method.MethodBody;
                Console.WriteLine($"\nMethod Body:");
                Console.WriteLine($"  Format: {(body.IsTiny ? "Tiny" : "Fat")}");
                Console.WriteLine($"  MaxStack: {body.MaxStack}");
                Console.WriteLine($"  CodeSize: {body.CodeSize} bytes");
                Console.WriteLine($"  LocalVarSigTok: 0x{body.LocalVarSigTok:X8}");

                if (body.ILCode.Length > 0)
                {
                    Console.WriteLine($"\n  IL Code (hex):");
                    PrintHexDump(body.ILCode, 16);
                }

                if (body.ExceptionClauses.Length > 0)
                {
                    Console.WriteLine($"\n  Exception Handling Clauses: {body.ExceptionClauses.Length}");
                    for (int i = 0; i < body.ExceptionClauses.Length; i++)
                    {
                        var clause = body.ExceptionClauses[i];
                        Console.WriteLine($"    [{i}] Flags: 0x{clause.Flags:X8}");
                        Console.WriteLine($"        Try: 0x{clause.TryOffset:X4} - 0x{clause.TryOffset + clause.TryLength:X4}");
                        Console.WriteLine($"        Handler: 0x{clause.HandlerOffset:X4} - 0x{clause.HandlerOffset + clause.HandlerLength:X4}");
                    }
                }
            }
            else
            {
                Console.WriteLine("\nNo method body (abstract or P/Invoke method)");
            }
        }

        private void PrintHexDump(byte[] data, int bytesPerLine = 16)
        {
            for (int i = 0; i < data.Length; i += bytesPerLine)
            {
                Console.Write($"    {i:X4}: ");
                
                // Print hex values
                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (i + j < data.Length)
                        Console.Write($"{data[i + j]:X2} ");
                    else
                        Console.Write("   ");
                }

                Console.Write(" | ");

                // Print ASCII representation
                for (int j = 0; j < bytesPerLine && i + j < data.Length; j++)
                {
                    byte b = data[i + j];
                    Console.Write(b >= 32 && b < 127 ? (char)b : '.');
                }

                Console.WriteLine();
            }
        }

        public void PrintHeapSizes()
        {
            Console.WriteLine("\n=== Heap Sizes ===\n");
            Console.WriteLine($"String Index Size: {_metadata.StringIndexSize} bytes");
            Console.WriteLine($"GUID Index Size: {_metadata.GuidIndexSize} bytes");
            Console.WriteLine($"Blob Index Size: {_metadata.BlobIndexSize} bytes");

            if (_metadata.Streams.ContainsKey("#Strings"))
                Console.WriteLine($"#Strings heap: {_metadata.Streams["#Strings"].Size} bytes");
            if (_metadata.Streams.ContainsKey("#GUID"))
                Console.WriteLine($"#GUID heap: {_metadata.Streams["#GUID"].Size} bytes");
            if (_metadata.Streams.ContainsKey("#Blob"))
                Console.WriteLine($"#Blob heap: {_metadata.Streams["#Blob"].Size} bytes");
            if (_metadata.Streams.ContainsKey("#US"))
                Console.WriteLine($"#US heap: {_metadata.Streams["#US"].Size} bytes");
        }

        public void PrintTextSectionAnalysis(List<Section> sections, byte[] fileData)
        {
            var analyzer = new TextSectionAnalyzer(_metadata, sections, fileData);
            analyzer.PrintStatistics();
        }
    }
}
