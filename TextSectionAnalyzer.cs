using System.Text;

namespace ECMA335Printer
{
    /// <summary>
    /// .text 节内容分析器
    /// 统计元数据流、方法体、静态字段初始数据等的大小和分布
    /// </summary>
    class TextSectionAnalyzer
    {
        private readonly MetadataRoot _metadata;
        private readonly List<Section> _sections;
        private readonly byte[] _fileData;

        public TextSectionAnalyzer(MetadataRoot metadata, List<Section> sections, byte[] fileData)
        {
            _metadata = metadata;
            _sections = sections;
            _fileData = fileData;
        }

        /// <summary>
        /// 打印完整的统计信息
        /// </summary>
        public void PrintStatistics()
        {
            Console.WriteLine("\n" + new string('=', 80));
            Console.WriteLine(".TEXT SECTION CONTENT ANALYSIS");
            Console.WriteLine(new string('=', 80));

            var textSection = _sections.FirstOrDefault(s => s.Name == ".text");
            if (textSection == null)
            {
                Console.WriteLine("Error: .text section not found");
                return;
            }

            Console.WriteLine($"\n.text Section Overview:");
            Console.WriteLine($"  Virtual Address: 0x{textSection.VirtualAddress:X8}");
            Console.WriteLine($"  Virtual Size: {textSection.VirtualSize:N0} bytes (0x{textSection.VirtualSize:X})");
            Console.WriteLine($"  Raw Data Size: {textSection.SizeOfRawData:N0} bytes (0x{textSection.SizeOfRawData:X})");

            // 统计元数据流
            var metadataStats = GetMetadataStreamsStatistics();
            PrintMetadataStreamsStatistics(metadataStats);

            // 统计方法体
            var methodBodyStats = GetMethodBodyStatistics();
            PrintMethodBodyStatistics(methodBodyStats);

            // 统计静态字段初始数据
            var staticFieldStats = GetStaticFieldDataStatistics();
            PrintStaticFieldDataStatistics(staticFieldStats);

            // 计算其他内容
            PrintOtherContentStatistics(textSection, metadataStats, methodBodyStats, staticFieldStats);
        }

        private (long totalSize, uint minRVA, uint maxRVA) GetMetadataStreamsStatistics()
        {
            long totalSize = 0;
            uint minRVA = uint.MaxValue;
            uint maxRVA = 0;

            foreach (var stream in _metadata.Streams.Values)
            {
                totalSize += stream.Size;
                // Note: Stream offsets are file offsets, not RVAs
            }

            // Add estimated header size
            totalSize += (uint)(20 + _metadata.Version.Length + 4 + 4 + _metadata.Streams.Count * 16);

            return (totalSize, minRVA, maxRVA);
        }

        private (long totalSize, uint minRVA, uint maxRVA, int count) GetMethodBodyStatistics()
        {
            long totalSize = 0;
            uint minRVA = uint.MaxValue;
            uint maxRVA = 0;
            int count = 0;

            foreach (var method in _metadata.MethodDefTable)
            {
                if (method.MethodBody != null && method.RVA > 0)
                {
                    count++;
                    var body = method.MethodBody;
                    uint headerSize = body.IsTiny ? 1u : 12u;
                    uint exceptionSize = body.ExceptionClauses.Length > 0 ? (uint)(4 + body.ExceptionClauses.Length * 24) : 0;
                    uint methodSize = headerSize + body.CodeSize + exceptionSize;
                    if (methodSize % 4 != 0)
                        methodSize += 4 - (methodSize % 4);
                    
                    totalSize += methodSize;

                    if (method.RVA < minRVA) minRVA = method.RVA;
                    uint methodEndRVA = method.RVA + methodSize;
                    if (methodEndRVA > maxRVA) maxRVA = methodEndRVA;
                }
            }

            return (totalSize, minRVA, maxRVA, count);
        }

        private (long totalSize, uint minRVA, uint maxRVA, int count) GetStaticFieldDataStatistics()
        {
            long totalSize = 0;
            uint minRVA = uint.MaxValue;
            uint maxRVA = 0;
            int count = _metadata.FieldRVATable.Length;

            if (count == 0)
                return (0, 0, 0, 0);

            var sortedFields = _metadata.FieldRVATable.OrderBy(f => f.RVA).ToArray();
            
            for (int i = 0; i < sortedFields.Length; i++)
            {
                uint size = 0;
                if (i < sortedFields.Length - 1)
                {
                    size = sortedFields[i + 1].RVA - sortedFields[i].RVA;
                }
                else
                {
                    size = 16; // Estimate for last field
                }
                totalSize += size;

                if (sortedFields[i].RVA < minRVA) minRVA = sortedFields[i].RVA;
                uint fieldEndRVA = sortedFields[i].RVA + size;
                if (fieldEndRVA > maxRVA) maxRVA = fieldEndRVA;
            }

            return (totalSize, minRVA, maxRVA, count);
        }

        /// <summary>
        /// 统计元数据流总大小
        /// </summary>
        private void PrintMetadataStreamsStatistics((long totalSize, uint minRVA, uint maxRVA) stats)
        {
            Console.WriteLine("\n" + new string('-', 80));
            Console.WriteLine("1. METADATA STREAMS");
            Console.WriteLine(new string('-', 80));

            uint totalMetadataSize = 0;

            // 元数据根头部大小（从BSJB签名到流头结束）
            // 这需要计算：签名(4) + 版本(4) + 保留(4) + 版本长度(4) + 版本字符串 + 对齐 + 标志(2) + 流数量(2) + 流头
            uint metadataHeaderSize = 0;
            
            // 获取第一个流的偏移（相对于元数据根）
            if (_metadata.Streams.Count > 0)
            {
                var firstStream = _metadata.Streams.Values.OrderBy(s => s.Offset).First();
                // 找到元数据根的起始位置
                uint metadataRootOffset = firstStream.Offset;
                foreach (var stream in _metadata.Streams.Values)
                {
                    if (stream.Offset < metadataRootOffset)
                        metadataRootOffset = stream.Offset;
                }
                
                // 元数据头 = 第一个流偏移 - 元数据根偏移
                // 但我们需要从CLI Header获取元数据根的实际位置
                // 简化处理：假设流偏移已经是绝对文件偏移
            }

            Console.WriteLine("\nMetadata Streams:");
            
            var streamNames = new[] { "#~", "#-", "#Strings", "#US", "#GUID", "#Blob" };
            foreach (var streamName in streamNames)
            {
                if (_metadata.Streams.ContainsKey(streamName))
                {
                    var stream = _metadata.Streams[streamName];
                    Console.WriteLine($"  {streamName,-12} {stream.Size,10:N0} bytes  (0x{stream.Size:X})");
                    totalMetadataSize += stream.Size;
                }
            }

            // 添加其他流
            foreach (var kvp in _metadata.Streams)
            {
                if (!streamNames.Contains(kvp.Key))
                {
                    Console.WriteLine($"  {kvp.Key,-12} {kvp.Value.Size,10:N0} bytes  (0x{kvp.Value.Size:X})");
                    totalMetadataSize += kvp.Value.Size;
                }
            }

            // 估算元数据头大小（包括流头）
            uint estimatedHeaderSize = (uint)(20 + _metadata.Version.Length + 4 + 4 + _metadata.Streams.Count * 16);
            totalMetadataSize += estimatedHeaderSize;

            Console.WriteLine($"\n  Metadata Header: ~{estimatedHeaderSize,8:N0} bytes  (estimated)");
            Console.WriteLine($"  {new string('-', 40)}");
            Console.WriteLine($"  TOTAL:          {stats.totalSize,10:N0} bytes  (0x{stats.totalSize:X})");
        }

        /// <summary>
        /// 统计方法体
        /// </summary>
        private void PrintMethodBodyStatistics((long totalSize, uint minRVA, uint maxRVA, int count) stats)
        {
            Console.WriteLine("\n" + new string('-', 80));
            Console.WriteLine("2. METHOD BODIES");
            Console.WriteLine(new string('-', 80));

            int methodBodyCount = 0;
            uint totalMethodBodySize = 0;
            int tinyMethodCount = 0;
            int fatMethodCount = 0;
            uint totalILSize = 0;
            uint totalHeaderSize = 0;
            uint totalExceptionSize = 0;

            foreach (var method in _metadata.MethodDefTable)
            {
                if (method.MethodBody != null)
                {
                    methodBodyCount++;
                    var body = method.MethodBody;

                    // IL代码大小
                    totalILSize += body.CodeSize;

                    // 方法头大小
                    uint headerSize = body.IsTiny ? 1u : 12u;
                    totalHeaderSize += headerSize;

                    // 异常处理子句大小
                    uint exceptionSize = 0;
                    if (body.ExceptionClauses.Length > 0)
                    {
                        // Fat格式：4字节头 + 每个子句24字节
                        exceptionSize = 4 + (uint)(body.ExceptionClauses.Length * 24);
                        totalExceptionSize += exceptionSize;
                    }

                    // 总大小（包括对齐）
                    uint methodSize = headerSize + body.CodeSize + exceptionSize;
                    // 方法体按4字节对齐
                    if (methodSize % 4 != 0)
                        methodSize += 4 - (methodSize % 4);

                    totalMethodBodySize += methodSize;

                    if (body.IsTiny)
                        tinyMethodCount++;
                    else
                        fatMethodCount++;
                }
            }

            Console.WriteLine($"\nMethod Body Statistics:");
            Console.WriteLine($"  Total Methods with Body: {methodBodyCount,10:N0}");
            Console.WriteLine($"  - Tiny Format:           {tinyMethodCount,10:N0}");
            Console.WriteLine($"  - Fat Format:            {fatMethodCount,10:N0}");
            Console.WriteLine($"\nSize Breakdown:");
            Console.WriteLine($"  Method Headers:          {totalHeaderSize,10:N0} bytes");
            Console.WriteLine($"  IL Code:                 {totalILSize,10:N0} bytes");
            Console.WriteLine($"  Exception Clauses:       {totalExceptionSize,10:N0} bytes");
            Console.WriteLine($"  Alignment Padding:       {totalMethodBodySize - totalHeaderSize - totalILSize - totalExceptionSize,10:N0} bytes (estimated)");
            Console.WriteLine($"  {new string('-', 40)}");
            Console.WriteLine($"  TOTAL:                   {stats.totalSize,10:N0} bytes  (0x{stats.totalSize:X})");
            Console.WriteLine($"\n  RVA Range: 0x{stats.minRVA:X8} - 0x{stats.maxRVA:X8}");
        }

        /// <summary>
        /// 统计静态字段初始数据
        /// </summary>
        private void PrintStaticFieldDataStatistics((long totalSize, uint minRVA, uint maxRVA, int count) stats)
        {
            Console.WriteLine("\n" + new string('-', 80));
            Console.WriteLine("3. STATIC FIELD INITIAL DATA");
            Console.WriteLine(new string('-', 80));

            if (_metadata.FieldRVATable.Length == 0)
            {
                Console.WriteLine("\n  No static fields with initial data (FieldRVA table is empty)");
                return;
            }

            int fieldCount = _metadata.FieldRVATable.Length;
            uint totalSize = 0;

            Console.WriteLine($"\nStatic Fields with Initial Data:");
            Console.WriteLine($"  Field Count: {fieldCount}");

            // 注意：准确计算每个字段的大小需要解析字段签名
            // 这里我们只能估算或者需要更复杂的签名解析
            // 简化处理：假设每个字段平均大小
            
            // 尝试通过RVA间隔估算大小
            var sortedFields = _metadata.FieldRVATable.OrderBy(f => f.RVA).ToArray();
            
            for (int i = 0; i < sortedFields.Length; i++)
            {
                uint size = 0;
                if (i < sortedFields.Length - 1)
                {
                    // 通过下一个字段的RVA计算大小
                    size = sortedFields[i + 1].RVA - sortedFields[i].RVA;
                }
                else
                {
                    // 最后一个字段，估算为平均大小或固定值
                    size = 16; // 估算值
                }
                totalSize += size;
            }

            Console.WriteLine($"  Estimated Total Size: {stats.totalSize,10:N0} bytes  (0x{stats.totalSize:X})");
            if (stats.count > 0)
            {
                Console.WriteLine($"  RVA Range: 0x{stats.minRVA:X8} - 0x{stats.maxRVA:X8}");
            }
            Console.WriteLine($"\n  Note: Size is estimated based on RVA intervals");
        }

        /// <summary>
        /// 计算其他内容
        /// </summary>
        private void PrintOtherContentStatistics(Section textSection,
            (long totalSize, uint minRVA, uint maxRVA) metadataStats,
            (long totalSize, uint minRVA, uint maxRVA, int count) methodBodyStats,
            (long totalSize, uint minRVA, uint maxRVA, int count) staticFieldStats)
        {
            Console.WriteLine("\n" + new string('-', 80));
            Console.WriteLine("4. OTHER CONTENT & ANALYSIS");
            Console.WriteLine(new string('-', 80));

            long metadataSize = metadataStats.totalSize;
            long methodBodySize = methodBodyStats.totalSize;
            long staticFieldSize = staticFieldStats.totalSize;

            // 检查静态字段是否与方法体重叠
            bool staticFieldsOverlapWithMethods = false;
            if (staticFieldStats.count > 0 && methodBodyStats.count > 0)
            {
                // 如果静态字段的RVA范围在方法体范围内，说明有重叠
                if (staticFieldStats.minRVA >= methodBodyStats.minRVA && staticFieldStats.minRVA < methodBodyStats.maxRVA)
                {
                    staticFieldsOverlapWithMethods = true;
                }
            }

            // 计算实际占用（避免重复计算）
            long actualCodeAndDataSize = methodBodySize;
            if (!staticFieldsOverlapWithMethods)
            {
                actualCodeAndDataSize += staticFieldSize;
            }

            long knownSize = metadataSize + actualCodeAndDataSize;
            long otherSize = (long)textSection.VirtualSize - knownSize;

            Console.WriteLine("\nOther Content in .text Section:");
            Console.WriteLine($"  CLI Header:              ~72 bytes  (fixed size)");
            Console.WriteLine($"  Import Address Table:    (variable)");
            Console.WriteLine($"  Unmanaged Code/Data:     (if any)");
            Console.WriteLine($"  Padding/Alignment:       (variable)");
            
            if (otherSize >= 0)
            {
                Console.WriteLine($"\n  Estimated Other Size:    {otherSize,10:N0} bytes  (0x{otherSize:X})");
            }
            else
            {
                long deficit = -otherSize;
                double deficitPercent = (double)deficit / textSection.VirtualSize * 100;
                
                Console.WriteLine($"\n  Note: Calculated sizes slightly exceed section size.");
                Console.WriteLine($"  This is normal due to:");
                Console.WriteLine($"    - Metadata header size estimation");
                Console.WriteLine($"    - Method body alignment calculations");
                Console.WriteLine($"    - Static field size estimation");
                Console.WriteLine($"  Estimation error: {deficit,10:N0} bytes ({deficitPercent:F2}%)");
            }

            // 显示RVA范围以帮助调试
            Console.WriteLine($"\nRVA Ranges:");
            if (methodBodyStats.count > 0)
                Console.WriteLine($"  Method Bodies:     0x{methodBodyStats.minRVA:X8} - 0x{methodBodyStats.maxRVA:X8}");
            if (staticFieldStats.count > 0)
            {
                Console.WriteLine($"  Static Fields:     0x{staticFieldStats.minRVA:X8} - 0x{staticFieldStats.maxRVA:X8}");
                if (staticFieldsOverlapWithMethods)
                {
                    Console.WriteLine($"  Note: Static field data is embedded within the code section");
                }
            }

            // 总结
            Console.WriteLine("\n" + new string('=', 80));
            Console.WriteLine("SUMMARY");
            Console.WriteLine(new string('=', 80));
            Console.WriteLine($"\n.text Section Total Size:    {textSection.VirtualSize,10:N0} bytes  (0x{textSection.VirtualSize:X})");
            Console.WriteLine($"\nContent Breakdown:");
            Console.WriteLine($"  1. Metadata Streams:       {metadataSize,10:N0} bytes  ({(double)metadataSize / textSection.VirtualSize * 100:F2}%)");
            Console.WriteLine($"  2. Method Bodies:          {methodBodySize,10:N0} bytes  ({(double)methodBodySize / textSection.VirtualSize * 100:F2}%)");
            
            if (staticFieldsOverlapWithMethods)
            {
                Console.WriteLine($"  3. Static Field Data:      {staticFieldSize,10:N0} bytes  ({(double)staticFieldSize / textSection.VirtualSize * 100:F2}%) [embedded in code]");
            }
            else
            {
                Console.WriteLine($"  3. Static Field Data:      {staticFieldSize,10:N0} bytes  ({(double)staticFieldSize / textSection.VirtualSize * 100:F2}%)");
            }
            
            if (otherSize >= 0)
            {
                Console.WriteLine($"  4. Other Content:          {otherSize,10:N0} bytes  ({(double)otherSize / textSection.VirtualSize * 100:F2}%)");
            }
            else
            {
                Console.WriteLine($"  4. Other Content:          <overlap detected>");
            }
            
            Console.WriteLine($"  {new string('-', 50)}");
            
            if (knownSize <= textSection.VirtualSize)
            {
                Console.WriteLine($"  Total Accounted:           {knownSize,10:N0} bytes  ({(double)knownSize / textSection.VirtualSize * 100:F2}%)");
                if (staticFieldsOverlapWithMethods)
                {
                    Console.WriteLine($"  (Static fields not double-counted)");
                }
            }
            else
            {
                long excess = knownSize - textSection.VirtualSize;
                double excessPercent = (double)excess / textSection.VirtualSize * 100;
                Console.WriteLine($"  Total Calculated:          {knownSize,10:N0} bytes  ({(double)knownSize / textSection.VirtualSize * 100:F2}%)");
                Console.WriteLine($"  Estimation Variance:       +{excess:N0} bytes (+{excessPercent:F2}%)");
                Console.WriteLine($"\n  Note: Small variance is expected due to estimation methods.");
            }
        }
    }
}
