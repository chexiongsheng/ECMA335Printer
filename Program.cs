

namespace ECMA335Printer
{
    /// <summary>
    /// ECMA-335 程序集打印工具
    /// 用于解析和显示.NET程序集的PE文件结构和元数据信息
    /// </summary>
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: ECMA335Printer <assembly-path> [-v]");
                Console.WriteLine("  -v: Verbose mode, print detailed metadata information");
                Console.WriteLine("Example: ECMA335Printer MyAssembly.dll");
                Console.WriteLine("Example: ECMA335Printer MyAssembly.dll -v");
                return;
            }

            string assemblyPath = args[0];
            bool verbose = args.Length > 1 && args[1] == "-v";

            if (!File.Exists(assemblyPath))
            {
                Console.WriteLine($"Error: File not found: {assemblyPath}");
                return;
            }

            try
            {
                PrintAssemblyTree(assemblyPath, verbose);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack: {ex.StackTrace}");
            }
        }

        static void PrintAssemblyTree(string assemblyPath, bool verbose)
        {
            var peFile = new PEFile(assemblyPath);
            peFile.Parse();
            peFile.PrintAssemblyInfo();

            if (verbose)
            {
                Console.WriteLine("\n" + new string('=', 80));
                Console.WriteLine("VERBOSE MODE: Detailed Metadata Information");
                Console.WriteLine(new string('=', 80));

                // Create printer with access to parser
                var metadata = peFile.Metadata;
                if (metadata != null)
                {
                    // We need to create a parser instance to pass to the printer
                    // For now, we'll need to expose the parser from PEFile or create a new one
                    PrintDetailedMetadata(peFile);
                }
            }
        }

        static void PrintDetailedMetadata(PEFile peFile)
        {
            var metadata = peFile.Metadata;
            if (metadata == null)
            {
                Console.WriteLine("No metadata available.");
                return;
            }

            // Create a temporary parser for string/GUID reading
            var stream = new FileStream(peFile.FilePath, FileMode.Open, FileAccess.Read);
            var reader = new BinaryReader(stream);
            var parser = new MetadataTablesParser(reader, metadata, peFile.FileData, peFile.Sections);

            var printer = new MetadataPrinter(metadata, parser);

            printer.PrintSummary();
            printer.PrintHeapSizes();
            printer.PrintModuleInfo();
            printer.PrintAssemblyInfo();
            printer.PrintTypeDefSummary(20);
            printer.PrintMethodDefSummary(20);

            // Print .text section analysis
            printer.PrintTextSectionAnalysis(peFile.Sections, peFile.FileData!);

            // Print signatures
            printer.PrintFieldSignatures(10);
            printer.PrintMethodSignatures(10);

            reader.Close();
            stream.Close();
        }
    }
}
