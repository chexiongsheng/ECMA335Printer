

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
                Console.WriteLine("Usage: ECMA335Printer <assembly-path>");
                Console.WriteLine("Example: ECMA335Printer MyAssembly.dll");
                return;
            }

            string assemblyPath = args[0];

            if (!File.Exists(assemblyPath))
            {
                Console.WriteLine($"Error: File not found: {assemblyPath}");
                return;
            }

            try
            {
                PrintAssemblyTree(assemblyPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack: {ex.StackTrace}");
            }
        }

        static void PrintAssemblyTree(string assemblyPath)
        {
            var peFile = new PEFile(assemblyPath);
            peFile.Parse();
            peFile.PrintAssemblyInfo();
        }
    }
}
