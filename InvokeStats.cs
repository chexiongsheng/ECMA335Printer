using System.Text.Json;

namespace ECMA335Printer
{
    /// <summary>
    /// 调用统计数据结构
    /// </summary>
    class InvokeStats
    {
        public string timestamp { get; set; } = "";
        public int totalAssemblies { get; set; }
        public int totalMethods { get; set; }
        public int totalInvocations { get; set; }
        public List<AssemblyStats> assemblies { get; set; } = new();
    }

    class AssemblyStats
    {
        public string assemblyName { get; set; } = "";
        public int totalMethods { get; set; }
        public int totalInvocations { get; set; }
        public List<MethodStats> methods { get; set; } = new();
    }

    class MethodStats
    {
        public string fullName { get; set; } = "";
        public int invocations { get; set; }
    }

    /// <summary>
    /// 调用统计解析器
    /// </summary>
    class InvokeStatsParser
    {
        private readonly string _statsFilePath;
        private InvokeStats? _stats;

        public InvokeStatsParser(string statsFilePath)
        {
            _statsFilePath = statsFilePath;
        }

        public void Parse()
        {
            if (!File.Exists(_statsFilePath))
            {
                throw new FileNotFoundException($"Stats file not found: {_statsFilePath}");
            }

            string json = File.ReadAllText(_statsFilePath);
            _stats = JsonSerializer.Deserialize<InvokeStats>(json);

            if (_stats == null)
            {
                throw new Exception("Failed to parse stats file");
            }
        }

        public HashSet<string> GetInvokedMethods(string assemblyName)
        {
            if (_stats == null)
            {
                throw new InvalidOperationException("Stats not parsed yet. Call Parse() first.");
            }

            var assembly = _stats.assemblies.FirstOrDefault(a => 
                a.assemblyName.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));

            if (assembly == null)
            {
                Console.WriteLine($"Warning: Assembly '{assemblyName}' not found in stats file");
                return new HashSet<string>();
            }

            var invokedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var method in assembly.methods)
            {
                invokedMethods.Add(method.fullName);
            }

            return invokedMethods;
        }
    }
}
