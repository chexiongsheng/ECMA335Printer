

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
                Console.WriteLine("Usage: ECMA335Printer <assembly-path> [-v] [-s0 <stats-file> [-d] [-trace]] [-s1 <stats-file> [-d] [-trace]]");
                Console.WriteLine("  -v: Verbose mode, print detailed metadata information");
                Console.WriteLine("  -s0 <stats-file>: Class-level trimming based on invoke statistics");
                Console.WriteLine("  -s1 <stats-file>: Method-level trimming based on invoke statistics");
                Console.WriteLine("  -d: Deep trimming - trim unused metadata after s0/s1 (must be used with -s0 or -s1)");
                Console.WriteLine("  -trace: Enable trace mode to show detailed processing steps with indentation");
                Console.WriteLine("Example: ECMA335Printer MyAssembly.dll");
                Console.WriteLine("Example: ECMA335Printer MyAssembly.dll -v");
                Console.WriteLine("Example: ECMA335Printer MyAssembly.dll -s0 invoke_stats.json");
                Console.WriteLine("Example: ECMA335Printer MyAssembly.dll -s1 invoke_stats.json");
                Console.WriteLine("Example: ECMA335Printer MyAssembly.dll -s0 invoke_stats.json -d");
                Console.WriteLine("Example: ECMA335Printer MyAssembly.dll -s1 invoke_stats.json -d");
                Console.WriteLine("Example: ECMA335Printer MyAssembly.dll -s0 invoke_stats.json -trace");
                return;
            }

            string assemblyPath = args[0];
            bool verbose = false;
            string? statsFile = null;
            int trimmingLevel = -1; // -1: no trimming, 0: class-level, 1: method-level
            bool enableDeepTrimming = false; // Deep trimming (metadata trimming after s0/s1)
            bool enableTrace = false; // Trace mode for debugging

            // Parse arguments
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "-v")
                {
                    verbose = true;
                }
                else if (args[i] == "-s0" && i + 1 < args.Length)
                {
                    statsFile = args[i + 1];
                    trimmingLevel = 0;
                    i++; // Skip next argument
                }
                else if (args[i] == "-s1" && i + 1 < args.Length)
                {
                    statsFile = args[i + 1];
                    trimmingLevel = 1;
                    i++; // Skip next argument
                }
                else if (args[i] == "-d")
                {
                    enableDeepTrimming = true;
                }
                else if (args[i] == "-trace")
                {
                    enableTrace = true;
                }
            }

            if (!File.Exists(assemblyPath))
            {
                Console.WriteLine($"Error: File not found: {assemblyPath}");
                return;
            }

            // Validate parameters
            if (enableDeepTrimming && trimmingLevel == -1)
            {
                Console.WriteLine("Error: -d parameter requires -s0 or -s1 to be specified");
                Console.WriteLine("Usage: ECMA335Printer <assembly-path> -s0 <stats-file> -d");
                Console.WriteLine("   or: ECMA335Printer <assembly-path> -s1 <stats-file> -d");
                return;
            }

            try
            {
                if (statsFile != null && trimmingLevel >= 0)
                {
                    // Perform trimming based on level
                    if (trimmingLevel == 0)
                    {
                        PerformClassLevelTrimming(assemblyPath, statsFile, enableDeepTrimming, enableTrace);
                    }
                    else if (trimmingLevel == 1)
                    {
                        PerformMethodLevelTrimming(assemblyPath, statsFile, enableDeepTrimming, enableTrace);
                    }
                }
                else
                {
                    // Normal print mode
                    PrintAssemblyTree(assemblyPath, verbose);
                }
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

        static void PerformClassLevelTrimming(string assemblyPath, string statsFile, bool enableDeepTrimming = false, bool enableTrace = false)
        {
            Console.WriteLine($"=== Class-Level Trimming ===");
            Console.WriteLine($"Assembly: {assemblyPath}");
            Console.WriteLine($"Stats file: {statsFile}");
            if (enableDeepTrimming)
            {
                Console.WriteLine($"Deep trimming: Enabled");
            }
            if (enableTrace)
            {
                Console.WriteLine($"Trace mode: Enabled");
            }

            // Parse the PE file
            var peFile = new PEFile(assemblyPath);
            peFile.Parse();

            // Parse the stats file
            var statsParser = new InvokeStatsParser(statsFile);
            statsParser.Parse();

            // Get assembly name from path
            string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            
            // Get invoked methods for this assembly
            var invokedMethods = statsParser.GetInvokedMethods(assemblyName);
            Console.WriteLine($"Found {invokedMethods.Count} invoked methods in stats");

            // Create trimmer and perform trimming
            var trimmer = new PETrimmer(peFile, invokedMethods, enableTrace);
            trimmer.TrimAtClassLevel();

            // Perform deep trimming if enabled
            if (enableDeepTrimming)
            {
                trimmer.TrimAtDeepLevel();
            }

            // Save trimmed file
            string outputPath = enableDeepTrimming ? assemblyPath + ".s0.d" : assemblyPath + ".s0";
            trimmer.SaveTrimmedFile(outputPath);

            Console.WriteLine($"\n=== Trimming Complete ===");
            Console.WriteLine($"Output file: {outputPath}");
        }

        static void PerformMethodLevelTrimming(string assemblyPath, string statsFile, bool enableDeepTrimming = false, bool enableTrace = false)
        {
            Console.WriteLine($"=== Method-Level Trimming ===");
            Console.WriteLine($"Assembly: {assemblyPath}");
            Console.WriteLine($"Stats file: {statsFile}");
            if (enableDeepTrimming)
            {
                Console.WriteLine($"Deep trimming: Enabled");
            }
            if (enableTrace)
            {
                Console.WriteLine($"Trace mode: Enabled");
            }

            // Parse the PE file
            var peFile = new PEFile(assemblyPath);
            peFile.Parse();

            // Parse the stats file
            var statsParser = new InvokeStatsParser(statsFile);
            statsParser.Parse();

            // Get assembly name from path
            string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            
            // Get invoked methods for this assembly
            var invokedMethods = statsParser.GetInvokedMethods(assemblyName);
            Console.WriteLine($"Found {invokedMethods.Count} invoked methods in stats");

            // Create trimmer and perform trimming
            var trimmer = new PETrimmer(peFile, invokedMethods, enableTrace);
            trimmer.TrimAtMethodLevel();

            // Perform deep trimming if enabled
            if (enableDeepTrimming)
            {
                trimmer.TrimAtDeepLevel();
            }

            // Save trimmed file
            string outputPath = enableDeepTrimming ? assemblyPath + ".s1.d" : assemblyPath + ".s1";
            trimmer.SaveTrimmedFile(outputPath);

            Console.WriteLine($"\n=== Trimming Complete ===");
            Console.WriteLine($"Output file: {outputPath}");
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
