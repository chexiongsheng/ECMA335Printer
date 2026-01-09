using System.Text;

namespace ECMA335Printer
{
    /// <summary>
    /// 字节操作委托：用于处理字节范围（清零或统计）
    /// </summary>
    delegate void ByteOperationDelegate(uint offset, uint length);

    /// <summary>
    /// PE文件剪裁器 - 支持类粒度和方法粒度剪裁
    /// </summary>
    class PETrimmer
    {
        private readonly PEFile _peFile;
        private readonly HashSet<string> _invokedMethods;
        private readonly HashSet<int> _invokedTypes; // Type indices that have invoked methods
        private readonly byte[] _fileData;
        private readonly MetadataRoot _metadata;
        private readonly List<Section> _sections;
        private long _totalBytesZeroed; // Track total bytes zeroed during trimming
        private long _remainingBytes; // Track remaining bytes (code/data)
        
        // Method Bodies statistics
        private int _trimmedMethodBodiesCount;
        private int _remainingMethodBodiesCount;
        private long _trimmedMethodBodiesBytes;
        private long _remainingMethodBodiesBytes;
        
        // Trimmed method bytes (including method body + metadata)
        private long _trimmedMethodBytes;
        private long _remainingMethodBytes;
        
        // Phase 1 (class-level) trimmed method bytes
        private long _phase1TrimmedMethodBytes;

        public PETrimmer(PEFile peFile, HashSet<string> invokedMethods)
        {
            _peFile = peFile;
            _invokedMethods = invokedMethods;
            _fileData = (byte[])peFile.FileData!.Clone(); // Clone to avoid modifying original
            _metadata = peFile.Metadata!;
            _sections = peFile.Sections;
            
            // Build invoked types set for better performance
            _invokedTypes = BuildInvokedTypesSet();
        }

        /// <summary>
        /// 构建已调用类型的集合
        /// </summary>
        private HashSet<int> BuildInvokedTypesSet()
        {
            var invokedTypes = new HashSet<int>();

            if (_metadata.TypeDefTable == null)
                return invokedTypes;

            Console.WriteLine("\n=== Building Invoked Types Set ===");

            // Extract type names from invoked methods
            // Convert generic type names from <T> format to `1 format
            var invokedTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var methodFullName in _invokedMethods)
            {
                // Extract type name (everything before the last dot)
                int lastDotIndex = methodFullName.LastIndexOf('.');
                if (lastDotIndex > 0)
                {
                    string typeName = methodFullName.Substring(0, lastDotIndex);
                    
                    // Convert generic type name: DictionaryView<T1,T2> -> DictionaryView`2
                    typeName = ConvertGenericTypeName(typeName);
                    
                    invokedTypeNames.Add(typeName);
                }
            }

            Console.WriteLine($"Extracted {invokedTypeNames.Count} unique type names from invoked methods");

            // Map type names to type indices
            for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
            {
                var typeDef = _metadata.TypeDefTable[typeIndex];
                string typeName = GetTypeName(typeDef);

                if (invokedTypeNames.Contains(typeName))
                {
                    invokedTypes.Add(typeIndex);
                }
            }

            Console.WriteLine($"Found {invokedTypes.Count} types with invoked methods");
            return invokedTypes;
        }

        /// <summary>
        /// 转换泛型类型名：从 <T> 格式转换为 `1 格式
        /// 例如：DictionaryView<T1,T2> -> DictionaryView`2
        ///       ListView<T> -> ListView`1
        /// </summary>
        private string ConvertGenericTypeName(string typeName)
        {
            int genericStart = typeName.IndexOf('<');
            if (genericStart < 0)
                return typeName; // Not a generic type

            // Count generic parameters
            int genericEnd = typeName.LastIndexOf('>');
            if (genericEnd < genericStart)
                return typeName; // Invalid format

            string genericPart = typeName.Substring(genericStart + 1, genericEnd - genericStart - 1);
            int paramCount = string.IsNullOrEmpty(genericPart) ? 0 : genericPart.Split(',').Length;

            // Convert to metadata format: TypeName`ParamCount
            string baseTypeName = typeName.Substring(0, genericStart);
            return $"{baseTypeName}`{paramCount}";
        }

        /// <summary>
        /// 执行方法粒度剪裁
        /// </summary>
        public void TrimAtMethodLevel()
        {
            Console.WriteLine("\n=== Starting Method-Level Trimming (includes Class-Level) ===");

            if (_metadata.TypeDefTable == null || _metadata.TypeDefTable.Length == 0)
            {
                Console.WriteLine("No TypeDef table found");
                return;
            }

            int totalClassCount = _metadata.TypeDefTable.Length;
            int trimmedClassCount = 0;
            int remainingClassCount = 0;
            int trimmedMethodCount = 0;
            int remainingMethodCount = 0;
            int totalMethodCount = _metadata.MethodDefTable?.Length ?? 0;
            _totalBytesZeroed = 0; // Reset byte counter
            _remainingBytes = 0; // Reset remaining bytes counter
            _trimmedMethodBytes = 0; // Reset trimmed method bytes counter

            long trimmedClassBytes = 0;

            // Two-phase trimming:
            // Phase 1: Trim entire classes that have no invoked methods (Class-Level)
            // Phase 2: For remaining classes, trim individual methods (Method-Level)
            
            Console.WriteLine("\n=== Phase 1: Class-Level Trimming ===");
            for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
            {
                var typeDef = _metadata.TypeDefTable[typeIndex];
                
                // Skip <Module> type (first type, index 0)
                if (typeIndex == 0)
                    continue;

                // Get type name
                string typeName = GetTypeName(typeDef);
                
                // Check if this type should be trimmed at class level
                if (ShouldTrimType(typeIndex))
                {
                    Console.WriteLine($"Trimming entire type: {typeName}");
                    long beforeTrim = _totalBytesZeroed;
                    WalkType(typeIndex, typeDef, ZeroBytes);
                    trimmedClassBytes += (_totalBytesZeroed - beforeTrim);
                    trimmedClassCount++;
                }
                else
                {
                    remainingClassCount++;
                }
            }

            // Record Phase 1 trimmed method bytes
            _phase1TrimmedMethodBytes = _trimmedMethodBytes;

            Console.WriteLine($"\n=== Phase 2: Method-Level Trimming (on remaining {remainingClassCount} types) ===");
            for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
            {
                var typeDef = _metadata.TypeDefTable[typeIndex];
                
                // Skip <Module> type (first type, index 0)
                if (typeIndex == 0)
                    continue;

                // Skip types that were already trimmed at class level
                if (ShouldTrimType(typeIndex))
                    continue;

                // Get type name
                string typeName = GetTypeName(typeDef);
                
                // Process each method in this remaining type
                uint methodStart = typeDef.MethodList;
                uint methodEnd;

                if (typeIndex < _metadata.TypeDefTable.Length - 1)
                {
                    methodEnd = _metadata.TypeDefTable[typeIndex + 1].MethodList;
                }
                else
                {
                    methodEnd = (uint)(_metadata.MethodDefTable?.Length ?? 0) + 1;
                }

                for (uint methodIdx = methodStart; methodIdx < methodEnd; methodIdx++)
                {
                    if (methodIdx == 0 || methodIdx > (_metadata.MethodDefTable?.Length ?? 0))
                        continue;

                    int methodIndex = (int)methodIdx - 1; // Convert to 0-based index
                    var method = _metadata.MethodDefTable[methodIndex];
                    string methodName = ReadString(method.Name);
                    string methodFullName = $"{typeName}.{methodName}";

                    // Check if this method should be trimmed
                    if (ShouldTrimMethod(methodFullName))
                    {
                        Console.WriteLine($"Trimming method: {methodFullName}");
                        WalkMethod(methodIndex, ZeroBytes);
                        trimmedMethodCount++;
                    }
                    else
                    {
                        // Count remaining bytes using a closure
                        WalkMethod(methodIndex, (offset, length) => _remainingBytes += length);
                        remainingMethodCount++;
                    }
                }

                // For remaining types, count type-level data (TypeDef rows, Field data, etc.)
                WalkTypeNonMethodData(typeIndex, typeDef, (offset, length) => _remainingBytes += length);
            }

            long trimmedBytes = _totalBytesZeroed;

            // Phase 3: Trim unused strings in #Strings heap
            Console.WriteLine($"\n=== Phase 3: String Trimming ===");
            var (stringsOriginalSize, trimmedStringBytes, stringsRemainingBytes) = TrimUnusedStrings();

            // Calculate base overhead (PE headers, shared metadata, heaps, other sections)
            long baseOverhead = CalculateBaseOverhead();
            long accountedBytes = _totalBytesZeroed + _remainingBytes + stringsRemainingBytes + baseOverhead;
            long unaccountedBytes = _fileData.Length - accountedBytes;

            Console.WriteLine($"\n=== Trimming Complete ===");
            Console.WriteLine($"Total types: {totalClassCount}");
            Console.WriteLine($"Trimmed types (entire): {trimmedClassCount}");
            Console.WriteLine($"Remaining types: {remainingClassCount}");
            Console.WriteLine($"\nTotal methods: {totalMethodCount}");
            Console.WriteLine($"Trimmed methods: {trimmedMethodCount}");
            Console.WriteLine($"Remaining methods: {remainingMethodCount}");
            Console.WriteLine($"\nMethod Bodies Statistics:");
            Console.WriteLine($"Trimmed method bodies: {_trimmedMethodBodiesCount}");
            Console.WriteLine($"Remaining method bodies: {_remainingMethodBodiesCount}");
            Console.WriteLine($"\nByte Statistics:");
            Console.WriteLine($"Total bytes: {_fileData.Length:N0}");
            Console.WriteLine($"Trimmed bytes (classes): {trimmedClassBytes:N0} ({(trimmedClassBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"  - Methods in class: {_phase1TrimmedMethodBytes:N0} ({(_phase1TrimmedMethodBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"  - Class metadata: {(trimmedClassBytes - _phase1TrimmedMethodBytes):N0} ({((trimmedClassBytes - _phase1TrimmedMethodBytes) * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Trimmed bytes (all methods): {_trimmedMethodBytes:N0} ({(_trimmedMethodBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"  - Method bodies: {_trimmedMethodBodiesBytes:N0} ({(_trimmedMethodBodiesBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"  - Method metadata: {(_trimmedMethodBytes - _trimmedMethodBodiesBytes):N0} ({((_trimmedMethodBytes - _trimmedMethodBodiesBytes) * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Trimmed bytes (strings): {trimmedStringBytes:N0} ({(trimmedStringBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Trimmed bytes (total): {_totalBytesZeroed:N0} ({(_totalBytesZeroed * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Remaining bytes (code/data): {_remainingBytes:N0} ({(_remainingBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"  - Methods: {_remainingMethodBytes:N0} ({(_remainingMethodBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"    - Method bodies: {_remainingMethodBodiesBytes:N0} ({(_remainingMethodBodiesBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"    - Method metadata: {(_remainingMethodBytes - _remainingMethodBodiesBytes):N0} ({((_remainingMethodBytes - _remainingMethodBodiesBytes) * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"  - Other metadata: {(_remainingBytes - _remainingMethodBytes):N0} ({((_remainingBytes - _remainingMethodBytes) * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Remaining bytes (strings): {stringsRemainingBytes:N0} ({(stringsRemainingBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Remaining bytes (total): {(_remainingBytes + stringsRemainingBytes):N0} ({((_remainingBytes + stringsRemainingBytes) * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Base overhead: {baseOverhead:N0} ({(baseOverhead * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"\n#Strings Heap Statistics:");
            Console.WriteLine($"  Original size: {stringsOriginalSize:N0} bytes");
            Console.WriteLine($"  Trimmed: {trimmedStringBytes:N0} bytes ({(trimmedStringBytes * 100.0 / stringsOriginalSize):F2}%)");
            Console.WriteLine($"  Remaining: {stringsRemainingBytes:N0} bytes ({(stringsRemainingBytes * 100.0 / stringsOriginalSize):F2}%)");
            if (unaccountedBytes != 0)
            {
                Console.WriteLine($"Unaccounted: {unaccountedBytes:N0} ({(unaccountedBytes * 100.0 / _fileData.Length):F2}%)");
            }
            if (trimmedClassCount > 0)
            {
                Console.WriteLine($"\nAverage bytes per trimmed type: {trimmedClassBytes / trimmedClassCount:N0}");
            }
            if (trimmedMethodCount > 0)
            {
                Console.WriteLine($"Average bytes per trimmed method: {_trimmedMethodBytes / trimmedMethodCount:N0}");
            }
            if (_trimmedMethodBodiesCount > 0)
            {
                Console.WriteLine($"Average bytes per trimmed method body: {_trimmedMethodBodiesBytes / _trimmedMethodBodiesCount:N0}");
            }
            if (remainingMethodCount > 0)
            {
                Console.WriteLine($"Average bytes per remaining method: {_remainingBytes / remainingMethodCount:N0}");
            }
            if (_remainingMethodBodiesCount > 0)
            {
                Console.WriteLine($"Average bytes per remaining method body: {_remainingMethodBodiesBytes / _remainingMethodBodiesCount:N0}");
            }
        }

        /// <summary>
        /// 执行类粒度剪裁
        /// </summary>
        public void TrimAtClassLevel()
        {
            Console.WriteLine("\n=== Starting Class-Level Trimming ===");

            if (_metadata.TypeDefTable == null || _metadata.TypeDefTable.Length == 0)
            {
                Console.WriteLine("No TypeDef table found");
                return;
            }

            int trimmedClassCount = 0;
            int remainingClassCount = 0;
            int totalClassCount = _metadata.TypeDefTable.Length;
            _totalBytesZeroed = 0; // Reset byte counter
            _remainingBytes = 0; // Reset remaining bytes counter

            // Single pass: Trim and count statistics
            Console.WriteLine("\n=== Performing Trimming ===");
            for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
            {
                var typeDef = _metadata.TypeDefTable[typeIndex];
                
                // Skip <Module> type (first type, index 0)
                if (typeIndex == 0)
                    continue;

                // Get type name
                string typeName = GetTypeName(typeDef);
                
                // Check if this type should be trimmed
                if (ShouldTrimType(typeIndex))
                {
                    Console.WriteLine($"Trimming type: {typeName}");
                    WalkType(typeIndex, typeDef, ZeroBytes);
                    trimmedClassCount++;
                }
                else
                {
                    // Count remaining bytes using a closure
                    WalkType(typeIndex, typeDef, (offset, length) => _remainingBytes += length);
                    remainingClassCount++;
                }
            }

            long trimmedBytes = _totalBytesZeroed;

            // Trim unused strings in #Strings heap
            Console.WriteLine($"\n=== String Trimming ===");
            var (stringsOriginalSize, trimmedStringBytes, stringsRemainingBytes) = TrimUnusedStrings();

            // Calculate base overhead (PE headers, shared metadata, heaps, other sections)
            long baseOverhead = CalculateBaseOverhead();
            long accountedBytes = _totalBytesZeroed + _remainingBytes + stringsRemainingBytes + baseOverhead;
            long unaccountedBytes = _fileData.Length - accountedBytes;

            Console.WriteLine($"\n=== Trimming Complete ===");
            Console.WriteLine($"Total types: {totalClassCount}");
            Console.WriteLine($"Trimmed types: {trimmedClassCount}");
            Console.WriteLine($"Remaining types: {remainingClassCount}");
            Console.WriteLine($"\nMethod Bodies Statistics:");
            Console.WriteLine($"Trimmed method bodies: {_trimmedMethodBodiesCount}");
            Console.WriteLine($"Remaining method bodies: {_remainingMethodBodiesCount}");
            Console.WriteLine($"\nByte Statistics:");
            Console.WriteLine($"Total bytes: {_fileData.Length:N0}");
            Console.WriteLine($"Trimmed bytes (types): {trimmedBytes:N0} ({(trimmedBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"  - Method bodies: {_trimmedMethodBodiesBytes:N0} ({(_trimmedMethodBodiesBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"  - Other metadata: {(trimmedBytes - _trimmedMethodBodiesBytes):N0} ({((trimmedBytes - _trimmedMethodBodiesBytes) * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Trimmed bytes (strings): {trimmedStringBytes:N0} ({(trimmedStringBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Trimmed bytes (total): {_totalBytesZeroed:N0} ({(_totalBytesZeroed * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Remaining bytes (code/data): {_remainingBytes:N0} ({(_remainingBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"  - Methods: {_remainingMethodBytes:N0} ({(_remainingMethodBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"    - Method bodies: {_remainingMethodBodiesBytes:N0} ({(_remainingMethodBodiesBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"    - Method metadata: {(_remainingMethodBytes - _remainingMethodBodiesBytes):N0} ({((_remainingMethodBytes - _remainingMethodBodiesBytes) * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"  - Other metadata: {(_remainingBytes - _remainingMethodBytes):N0} ({((_remainingBytes - _remainingMethodBytes) * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Remaining bytes (strings): {stringsRemainingBytes:N0} ({(stringsRemainingBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Remaining bytes (total): {(_remainingBytes + stringsRemainingBytes):N0} ({((_remainingBytes + stringsRemainingBytes) * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Base overhead: {baseOverhead:N0} ({(baseOverhead * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"\n#Strings Heap Statistics:");
            Console.WriteLine($"  Original size: {stringsOriginalSize:N0} bytes");
            Console.WriteLine($"  Trimmed: {trimmedStringBytes:N0} bytes ({(trimmedStringBytes * 100.0 / stringsOriginalSize):F2}%)");
            Console.WriteLine($"  Remaining: {stringsRemainingBytes:N0} bytes ({(stringsRemainingBytes * 100.0 / stringsOriginalSize):F2}%)");
            if (unaccountedBytes != 0)
            {
                Console.WriteLine($"Unaccounted: {unaccountedBytes:N0} ({(unaccountedBytes * 100.0 / _fileData.Length):F2}%)");
            }
            if (trimmedClassCount > 0)
            {
                Console.WriteLine($"\nAverage bytes per trimmed type: {trimmedBytes / trimmedClassCount:N0}");
            }
            if (_trimmedMethodBodiesCount > 0)
            {
                Console.WriteLine($"Average bytes per trimmed method body: {_trimmedMethodBodiesBytes / _trimmedMethodBodiesCount:N0}");
            }
            if (remainingClassCount > 0)
            {
                Console.WriteLine($"Average bytes per remaining type: {_remainingBytes / remainingClassCount:N0}");
            }
            if (_remainingMethodBodiesCount > 0)
            {
                Console.WriteLine($"Average bytes per remaining method body: {_remainingMethodBodiesBytes / _remainingMethodBodiesCount:N0}");
            }
        }

        /// <summary>
        /// 判断类型是否应该被剪裁
        /// </summary>
        private bool ShouldTrimType(int typeIndex)
        {
            // Simply check if the type is in the invoked types set
            return !_invokedTypes.Contains(typeIndex);
        }

        /// <summary>
        /// 判断方法是否应该被剪裁（方法级别剪裁）
        /// </summary>
        private bool ShouldTrimMethod(string methodFullName)
        {
            // Normalize the method name from metadata format to stats format
            // .ctor -> _ctor, .cctor -> _cctor
            string normalizedName = NormalizeMethodNameForComparison(methodFullName);
            
            // Check if the method is in the invoked methods set
            return !_invokedMethods.Contains(normalizedName);
        }

        /// <summary>
        /// 规范化方法名用于比较
        /// 将元数据中的构造函数名称转换为invoke_stats文件中的格式
        /// TypeName..ctor -> TypeName._ctor
        /// TypeName..cctor -> TypeName._cctor
        /// </summary>
        private string NormalizeMethodNameForComparison(string methodFullName)
        {
            // Check for constructor names (they have double dots in metadata)
            // TypeName..ctor -> TypeName._ctor
            // TypeName..cctor -> TypeName._cctor
            
            if (methodFullName.EndsWith("..ctor"))
            {
                return methodFullName.Substring(0, methodFullName.Length - 6) + "._ctor";
            }
            else if (methodFullName.EndsWith("..cctor"))
            {
                return methodFullName.Substring(0, methodFullName.Length - 7) + "._cctor";
            }

            return methodFullName;
        }

        /// <summary>
        /// 遍历类型的非方法数据（字段、属性、事件、TypeDef行等）
        /// 用于方法级别剪裁时统计类型级别的数据
        /// </summary>
        private void WalkTypeNonMethodData(int typeIndex, TypeDefRow typeDef, ByteOperationDelegate operation)
        {
            // 1. Walk all fields of this type
            WalkTypeFields(typeIndex, typeDef, operation);

            // 2. Walk properties
            WalkTypeProperties(typeIndex, operation);

            // 3. Walk events
            WalkTypeEvents(typeIndex, operation);

            // 4. Process TypeDef row data (but keep the row structure)
            ProcessTypeDefRow(typeIndex, operation);
        }

        /// <summary>
        /// 遍历指定类型（处理所有相关数据）
        /// </summary>
        private void WalkType(int typeIndex, TypeDefRow typeDef, ByteOperationDelegate operation)
        {
            // 1. Walk all methods of this type
            WalkTypeMethods(typeIndex, typeDef, operation);

            // 2. Walk all fields of this type
            WalkTypeFields(typeIndex, typeDef, operation);

            // 3. Walk properties
            WalkTypeProperties(typeIndex, operation);

            // 4. Walk events
            WalkTypeEvents(typeIndex, operation);

            // 5. Process TypeDef row data (but keep the row structure)
            ProcessTypeDefRow(typeIndex, operation);
        }

        /// <summary>
        /// 遍历类型的所有方法
        /// </summary>
        private void WalkTypeMethods(int typeIndex, TypeDefRow typeDef, ByteOperationDelegate operation)
        {
            uint methodStart = typeDef.MethodList;
            uint methodEnd;

            if (typeIndex < _metadata.TypeDefTable!.Length - 1)
            {
                methodEnd = _metadata.TypeDefTable[typeIndex + 1].MethodList;
            }
            else
            {
                methodEnd = (uint)(_metadata.MethodDefTable?.Length ?? 0) + 1;
            }

            for (uint methodIdx = methodStart; methodIdx < methodEnd; methodIdx++)
            {
                if (methodIdx == 0 || methodIdx > (_metadata.MethodDefTable?.Length ?? 0))
                    continue;

                WalkMethod((int)methodIdx - 1, operation); // Convert to 0-based index
            }
        }

        /// <summary>
        /// 遍历单个方法
        /// </summary>
        private void WalkMethod(int methodIndex, ByteOperationDelegate operation)
        {
            if (_metadata.MethodDefTable == null || methodIndex >= _metadata.MethodDefTable.Length)
                return;

            var method = _metadata.MethodDefTable[methodIndex];

            // Track the total bytes for this method (body + metadata)
            long beforeZeroed = _totalBytesZeroed;
            long beforeRemaining = _remainingBytes;

            // 1. Process method body (IL code)
            if (method.RVA != 0)
            {
                // Track method body statistics separately
                long beforeBodyZeroed = _totalBytesZeroed;
                long beforeBodyRemaining = _remainingBytes;
                
                ProcessMethodBody(method.RVA, operation);
                
                long afterBodyZeroed = _totalBytesZeroed;
                long afterBodyRemaining = _remainingBytes;
                
                // Calculate the difference for method body
                long bodyZeroedDiff = afterBodyZeroed - beforeBodyZeroed;
                long bodyRemainingDiff = afterBodyRemaining - beforeBodyRemaining;
                
                if (bodyZeroedDiff > 0)
                {
                    _trimmedMethodBodiesCount++;
                    _trimmedMethodBodiesBytes += bodyZeroedDiff;
                }
                else if (bodyRemainingDiff > 0)
                {
                    _remainingMethodBodiesCount++;
                    _remainingMethodBodiesBytes += bodyRemainingDiff;
                }
            }

            // 2. Process method signature in Blob heap
            if (method.Signature != 0)
            {
                ProcessBlobData(method.Signature, operation);
            }

            // 3. Process parameters
            WalkMethodParameters(methodIndex, method, operation);

            // 4. Process MethodDef row in metadata table
            ProcessMethodDefRow(methodIndex, operation);
            
            // Calculate the total bytes for this method (body + metadata)
            long afterZeroed = _totalBytesZeroed;
            long afterRemaining = _remainingBytes;
            
            long methodZeroedDiff = afterZeroed - beforeZeroed;
            long methodRemainingDiff = afterRemaining - beforeRemaining;
            
            if (methodZeroedDiff > 0)
            {
                _trimmedMethodBytes += methodZeroedDiff;
            }
            else if (methodRemainingDiff > 0)
            {
                _remainingMethodBytes += methodRemainingDiff;
            }
        }

        /// <summary>
        /// 遍历方法参数
        /// </summary>
        private void WalkMethodParameters(int methodIndex, MethodDefRow method, ByteOperationDelegate operation)
        {
            if (_metadata.ParamTable == null || _metadata.ParamTable.Length == 0)
                return;

            uint paramStart = method.ParamList;
            uint paramEnd;

            if (methodIndex < _metadata.MethodDefTable!.Length - 1)
            {
                paramEnd = _metadata.MethodDefTable[methodIndex + 1].ParamList;
            }
            else
            {
                paramEnd = (uint)_metadata.ParamTable.Length + 1;
            }

            for (uint paramIdx = paramStart; paramIdx < paramEnd; paramIdx++)
            {
                if (paramIdx == 0 || paramIdx > _metadata.ParamTable.Length)
                    continue;

                ProcessParamRow((int)paramIdx - 1, operation);
            }
        }

        /// <summary>
        /// 遍历类型的所有字段
        /// </summary>
        private void WalkTypeFields(int typeIndex, TypeDefRow typeDef, ByteOperationDelegate operation)
        {
            uint fieldStart = typeDef.FieldList;
            uint fieldEnd;

            if (typeIndex < _metadata.TypeDefTable!.Length - 1)
            {
                fieldEnd = _metadata.TypeDefTable[typeIndex + 1].FieldList;
            }
            else
            {
                fieldEnd = (uint)(_metadata.FieldTable?.Length ?? 0) + 1;
            }

            for (uint fieldIdx = fieldStart; fieldIdx < fieldEnd; fieldIdx++)
            {
                if (fieldIdx == 0 || fieldIdx > (_metadata.FieldTable?.Length ?? 0))
                    continue;

                WalkField((int)fieldIdx - 1, operation);
            }
        }

        /// <summary>
        /// 遍历单个字段
        /// </summary>
        private void WalkField(int fieldIndex, ByteOperationDelegate operation)
        {
            if (_metadata.FieldTable == null || fieldIndex >= _metadata.FieldTable.Length)
                return;

            var field = _metadata.FieldTable[fieldIndex];

            // 1. Process field signature in Blob heap
            if (field.Signature != 0)
            {
                ProcessBlobData(field.Signature, operation);
            }

            // 2. Process FieldRVA if exists
            if (_metadata.FieldRVATable != null)
            {
                foreach (var fieldRVA in _metadata.FieldRVATable)
                {
                    if (fieldRVA.Field == fieldIndex + 1) // 1-based
                    {
                        ProcessFieldRVAData(fieldRVA.RVA, operation);
                    }
                }
            }

            // 3. Process Field row in metadata table
            ProcessFieldRow(fieldIndex, operation);
        }

        /// <summary>
        /// 遍历类型的属性
        /// </summary>
        private void WalkTypeProperties(int typeIndex, ByteOperationDelegate operation)
        {
            if (_metadata.PropertyMapTable == null || _metadata.PropertyTable == null)
                return;

            foreach (var propMap in _metadata.PropertyMapTable)
            {
                if (propMap.Parent == typeIndex + 1) // 1-based
                {
                    // Find property range
                    // This is simplified - in real implementation need to handle ranges properly
                    // For now, just zero the property map entry
                }
            }
        }

        /// <summary>
        /// 遍历类型的事件
        /// </summary>
        private void WalkTypeEvents(int typeIndex, ByteOperationDelegate operation)
        {
            if (_metadata.EventMapTable == null || _metadata.EventTable == null)
                return;

            foreach (var eventMap in _metadata.EventMapTable)
            {
                if (eventMap.Parent == typeIndex + 1) // 1-based
                {
                    // Find event range
                    // This is simplified - in real implementation need to handle ranges properly
                }
            }
        }

        #region Process Operations

        /// <summary>
        /// 处理方法体
        /// </summary>
        private void ProcessMethodBody(uint rva, ByteOperationDelegate operation)
        {
            try
            {
                uint offset = RVAToFileOffset(rva);
                if (offset == 0 || offset >= _fileData.Length)
                    return;

                byte firstByte = _fileData[offset];

                // Check if tiny or fat format
                if ((firstByte & 0x03) == 0x02) // Tiny format
                {
                    uint codeSize = (uint)(firstByte >> 2);
                    uint totalSize = 1 + codeSize;
                    operation(offset, totalSize);
                }
                else if ((firstByte & 0x03) == 0x03) // Fat format
                {
                    uint codeSize = BitConverter.ToUInt32(_fileData, (int)offset + 4);
                    uint totalSize = 12 + codeSize; // Fat header is 12 bytes
                    
                    // Also handle exception clauses if present
                    ushort flags = BitConverter.ToUInt16(_fileData, (int)offset);
                    if ((flags & 0x08) != 0) // MoreSects flag
                    {
                        uint sectOffset = offset + 12 + codeSize;
                        sectOffset = (sectOffset + 3) & ~3u; // Align to 4 bytes
                        
                        if (sectOffset < _fileData.Length)
                        {
                            byte sectFlags = _fileData[sectOffset];
                            if ((sectFlags & 0x01) != 0) // EHTable
                            {
                                bool fatFormat = (sectFlags & 0x40) != 0;
                                uint dataSize = fatFormat ?
                                    BitConverter.ToUInt32(_fileData, (int)sectOffset) >> 8 :
                                    _fileData[sectOffset + 1];
                                totalSize = sectOffset - offset + dataSize;
                            }
                        }
                    }
                    
                    operation(offset, totalSize);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to process method body at RVA 0x{rva:X}: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理Blob数据
        /// </summary>
        private void ProcessBlobData(uint blobOffset, ByteOperationDelegate operation)
        {
            if (!_metadata.Streams.ContainsKey("#Blob"))
                return;

            var blobStream = _metadata.Streams["#Blob"];
            uint fileOffset = blobStream.Offset + blobOffset;

            if (fileOffset >= _fileData.Length)
                return;

            // Read compressed length
            int pos = (int)fileOffset;
            int length;
            int headerSize;

            if ((_fileData[pos] & 0x80) == 0)
            {
                length = _fileData[pos];
                headerSize = 1;
            }
            else if ((_fileData[pos] & 0xC0) == 0x80)
            {
                length = ((_fileData[pos] & 0x3F) << 8) | _fileData[pos + 1];
                headerSize = 2;
            }
            else if ((_fileData[pos] & 0xE0) == 0xC0)
            {
                length = ((_fileData[pos] & 0x1F) << 24) | (_fileData[pos + 1] << 16) | 
                         (_fileData[pos + 2] << 8) | _fileData[pos + 3];
                headerSize = 4;
            }
            else
            {
                return;
            }

            // Process the blob data (but keep the length header)
            operation((uint)(pos + headerSize), (uint)length);
        }

        /// <summary>
        /// 处理FieldRVA数据
        /// </summary>
        private void ProcessFieldRVAData(uint rva, ByteOperationDelegate operation)
        {
            try
            {
                // 计数size比较复杂，考虑到这块不是很大，可以选择不清零
                //uint offset = RVAToFileOffset(rva);
                // We don't know the exact size, so zero a reasonable amount
                // In practice, you'd need to determine the field size from its type
                //ZeroBytes(offset, 256); // Conservative estimate
            }
            catch
            {
                // Ignore errors
            }
        }

        /// <summary>
        /// 处理TypeDef行
        /// </summary>
        private void ProcessTypeDefRow(int typeIndex, ByteOperationDelegate operation)
        {
            if (_metadata.TypeDefTable == null || typeIndex >= _metadata.TypeDefTable.Length)
                return;

            // Calculate TypeDef row size
            // TypeDef: Flags(4) + TypeName(StringIndex) + TypeNamespace(StringIndex) + 
            //          Extends(CodedIndex) + FieldList(TableIndex) + MethodList(TableIndex)
            int rowSize = 4; // Flags
            rowSize += _metadata.StringIndexSize; // TypeName
            rowSize += _metadata.StringIndexSize; // TypeNamespace
            rowSize += GetCodedIndexSize(new[] { 0x02, 0x01, 0x1B }); // Extends (TypeDefOrRef)
            rowSize += GetTableIndexSize(0x04); // FieldList
            rowSize += GetTableIndexSize(0x06); // MethodList

            uint rowOffset = GetTableRowOffset(0x02, typeIndex); // 0x02 = TypeDef table
            if (rowOffset > 0)
            {
                // Process only the first 4 fields, keep FieldList and MethodList for index integrity
                int bytesToZero = 4 + _metadata.StringIndexSize * 2 + GetCodedIndexSize(new[] { 0x02, 0x01, 0x1B });
                operation(rowOffset, (uint)bytesToZero);
            }
        }

        /// <summary>
        /// 处理MethodDef行
        /// </summary>
        private void ProcessMethodDefRow(int methodIndex, ByteOperationDelegate operation)
        {
            if (_metadata.MethodDefTable == null || methodIndex >= _metadata.MethodDefTable.Length)
                return;

            // Calculate MethodDef row size
            // MethodDef: RVA(4) + ImplFlags(2) + Flags(2) + Name(StringIndex) + 
            //            Signature(BlobIndex) + ParamList(TableIndex)
            int rowSize = 4 + 2 + 2; // RVA + ImplFlags + Flags
            rowSize += _metadata.StringIndexSize; // Name
            rowSize += _metadata.BlobIndexSize; // Signature
            rowSize += GetTableIndexSize(0x08); // ParamList

            uint rowOffset = GetTableRowOffset(0x06, methodIndex); // 0x06 = MethodDef table
            if (rowOffset > 0)
            {
                // Process all fields except ParamList (keep it for index integrity)
                int bytesToZero = 4 + 2 + 2 + _metadata.StringIndexSize + _metadata.BlobIndexSize;
                operation(rowOffset, (uint)bytesToZero);
            }
        }

        /// <summary>
        /// 处理Field行
        /// </summary>
        private void ProcessFieldRow(int fieldIndex, ByteOperationDelegate operation)
        {
            if (_metadata.FieldTable == null || fieldIndex >= _metadata.FieldTable.Length)
                return;

            // Calculate Field row size
            // Field: Flags(2) + Name(StringIndex) + Signature(BlobIndex)
            int rowSize = 2; // Flags
            rowSize += _metadata.StringIndexSize; // Name
            rowSize += _metadata.BlobIndexSize; // Signature

            uint rowOffset = GetTableRowOffset(0x04, fieldIndex); // 0x04 = Field table
            if (rowOffset > 0)
            {
                operation(rowOffset, (uint)rowSize);
            }
        }

        /// <summary>
        /// 处理Param行
        /// </summary>
        private void ProcessParamRow(int paramIndex, ByteOperationDelegate operation)
        {
            if (_metadata.ParamTable == null || paramIndex >= _metadata.ParamTable.Length)
                return;

            // Calculate Param row size
            // Param: Flags(2) + Sequence(2) + Name(StringIndex)
            int rowSize = 2 + 2; // Flags + Sequence
            rowSize += _metadata.StringIndexSize; // Name

            uint rowOffset = GetTableRowOffset(0x08, paramIndex); // 0x08 = Param table
            if (rowOffset > 0)
            {
                operation(rowOffset, (uint)rowSize);
            }
        }

        /// <summary>
        /// 清零指定范围的字节（用于实际剪裁）
        /// </summary>
        private void ZeroBytes(uint offset, uint length)
        {
            if (offset + length > _fileData.Length)
                length = (uint)(_fileData.Length - offset);

            for (uint i = 0; i < length; i++)
            {
                _fileData[offset + i] = 0;
            }
            
            _totalBytesZeroed += length; // Track zeroed bytes
        }

        /// <summary>
        /// 统计字节数（用于统计分析，不修改文件数据）
        /// </summary>


        #endregion

        #region Helper Methods

        /// <summary>
        /// 获取表行在文件中的偏移量
        /// </summary>
        private uint GetTableRowOffset(int tableId, int rowIndex)
        {
            // Get #~ or #- stream
            string streamName = _metadata.Streams.ContainsKey("#~") ? "#~" : "#-";
            if (!_metadata.Streams.ContainsKey(streamName))
                return 0;

            var stream = _metadata.Streams[streamName];
            uint offset = stream.Offset;

            // Skip stream header (24 bytes: Reserved(4) + MajorVersion(1) + MinorVersion(1) + 
            //                                HeapSizes(1) + Reserved(1) + Valid(8) + Sorted(8))
            offset += 24;

            // Skip row counts (4 bytes per valid table)
            for (int i = 0; i < 64; i++)
            {
                if (_metadata.TableRowCounts[i] > 0)
                {
                    offset += 4;
                }
            }

            // Calculate offset to the target table
            for (int i = 0; i < tableId; i++)
            {
                if (_metadata.TableRowCounts[i] > 0)
                {
                    int rowSize = GetTableRowSize(i);
                    offset += (uint)(rowSize * _metadata.TableRowCounts[i]);
                }
            }

            // Add offset for the specific row
            int targetRowSize = GetTableRowSize(tableId);
            offset += (uint)(targetRowSize * rowIndex);

            return offset;
        }

        /// <summary>
        /// 获取表行的大小（字节数）
        /// </summary>
        private int GetTableRowSize(int tableId)
        {
            switch (tableId)
            {
                case 0x00: // Module
                    return 2 + _metadata.StringIndexSize + _metadata.GuidIndexSize * 3;
                case 0x01: // TypeRef
                    return GetCodedIndexSize(new[] { 0x00, 0x1A, 0x23, 0x01 }) + _metadata.StringIndexSize * 2;
                case 0x02: // TypeDef
                    return 4 + _metadata.StringIndexSize * 2 + GetCodedIndexSize(new[] { 0x02, 0x01, 0x1B }) +
                           GetTableIndexSize(0x04) + GetTableIndexSize(0x06);
                case 0x04: // Field
                    return 2 + _metadata.StringIndexSize + _metadata.BlobIndexSize;
                case 0x06: // MethodDef
                    return 4 + 2 + 2 + _metadata.StringIndexSize + _metadata.BlobIndexSize + GetTableIndexSize(0x08);
                case 0x08: // Param
                    return 2 + 2 + _metadata.StringIndexSize;
                // Add more tables as needed
                default:
                    return 0;
            }
        }

        /// <summary>
        /// 获取表索引的大小（2或4字节）
        /// </summary>
        private int GetTableIndexSize(int tableId)
        {
            if (tableId < 0 || tableId >= _metadata.TableRowCounts.Length)
                return 2;
            return _metadata.TableRowCounts[tableId] < 65536 ? 2 : 4;
        }

        /// <summary>
        /// 获取编码索引的大小
        /// </summary>
        private int GetCodedIndexSize(int[] tables)
        {
            // Calculate the maximum row count among the tables
            int maxRows = 0;
            foreach (int tableId in tables)
            {
                if (tableId < _metadata.TableRowCounts.Length)
                {
                    maxRows = Math.Max(maxRows, _metadata.TableRowCounts[tableId]);
                }
            }

            // Coded index uses tag bits, so we need to check if (maxRows << tagBits) fits in 16 bits
            int tagBits = 0;
            int tableCount = tables.Length;
            while ((1 << tagBits) < tableCount)
                tagBits++;

            return (maxRows << tagBits) < 65536 ? 2 : 4;
        }

        /// <summary>
        /// 计算基础开销（PE头部、共享元数据、堆数据、其他节等）
        /// </summary>
        private long CalculateBaseOverhead()
        {
            long overhead = 0;

            // 1. PE Headers (DOS Header, PE Header, Optional Header, Section Headers)
            // Typically ends where first section begins
            if (_sections.Count > 0)
            {
                overhead += _sections[0].PointerToRawData;
            }

            // 2. Metadata Root and Stream Headers
            // This is the metadata header before the actual streams
            if (_metadata.Streams.Count > 0)
            {
                // Estimate: Metadata root header + stream headers
                // Typically around 100-200 bytes depending on number of streams
                overhead += 100 + (_metadata.Streams.Count * 20);
            }

            // 3. Shared Heaps (#US, #GUID, #Blob)
            // Note: #Strings is handled separately in string trimming
            if (_metadata.Streams.ContainsKey("#US"))
                overhead += _metadata.Streams["#US"].Data.Length;
            if (_metadata.Streams.ContainsKey("#GUID"))
                overhead += _metadata.Streams["#GUID"].Data.Length;
            if (_metadata.Streams.ContainsKey("#Blob"))
                overhead += _metadata.Streams["#Blob"].Data.Length;

            // 4. Shared Metadata Tables (Module, Assembly, AssemblyRef, TypeRef, MemberRef, etc.)
            // These are not specific to any TypeDef
            if (_metadata.Streams.ContainsKey("#~"))
            {
                // Estimate table header size
                overhead += 24; // Table stream header

                // Add sizes of shared tables
                int[] sharedTables = { 0x00, 0x01, 0x0A, 0x20, 0x23, 0x26, 0x27 }; // Module, TypeRef, MemberRef, Assembly, AssemblyRef, File, ExportedType
                foreach (int tableId in sharedTables)
                {
                    if (tableId < _metadata.TableRowCounts.Length && _metadata.TableRowCounts[tableId] > 0)
                    {
                        int rowSize = EstimateTableRowSize(tableId);
                        overhead += _metadata.TableRowCounts[tableId] * rowSize;
                    }
                }
            }

            // 5. Other sections (.rsrc, .reloc, etc.)
            foreach (var section in _sections)
            {
                if (section.Name != ".text")
                {
                    overhead += section.SizeOfRawData;
                }
            }

            return overhead;
        }

        /// <summary>
        /// 估算元数据表行的大小（简化版本）
        /// </summary>
        private int EstimateTableRowSize(int tableId)
        {
            // Simplified estimation based on ECMA-335 table definitions
            switch (tableId)
            {
                case 0x00: return 10; // Module
                case 0x01: return 6;  // TypeRef
                case 0x0A: return 6;  // MemberRef
                case 0x20: return 16; // Assembly
                case 0x23: return 12; // AssemblyRef
                case 0x26: return 4;  // File
                case 0x27: return 8;  // ExportedType
                default: return 8;    // Default estimate
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
            return 0;
        }

        private string GetTypeName(TypeDefRow typeDef)
        {
            string ns = ReadString(typeDef.TypeNamespace);
            string name = ReadString(typeDef.TypeName);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        private string GetMethodFullName(string typeName, MethodDefRow method)
        {
            string methodName = ReadString(method.Name);
            return $"{typeName}.{methodName}";
        }

        private string ReadString(uint offset)
        {
            if (!_metadata.Streams.ContainsKey("#Strings") || offset == 0)
                return "";

            var data = _metadata.Streams["#Strings"].Data;
            if (offset >= data.Length)
                return "";

            int end = (int)offset;
            while (end < data.Length && data[end] != 0)
                end++;

            return Encoding.UTF8.GetString(data, (int)offset, end - (int)offset);
        }

        #endregion

        #region String Trimming

        /// <summary>
        /// 剪裁#Strings堆中未使用的字符串
        /// </summary>
        /// <returns>Tuple of (originalSize, trimmedBytes, remainingBytes)</returns>
        private (long originalSize, long trimmedBytes, long remainingBytes) TrimUnusedStrings()
        {
            if (!_metadata.Streams.ContainsKey("#Strings"))
            {
                Console.WriteLine("No #Strings heap found");
                return (0, 0, 0);
            }

            var stringsStream = _metadata.Streams["#Strings"];
            long originalSize = stringsStream.Data.Length;
            long bytesZeroedBefore = _totalBytesZeroed;

            // Step 1: Collect all used string offsets
            Console.WriteLine("Collecting used string offsets...");
            HashSet<uint> usedStringOffsets = CollectUsedStringOffsets();
            Console.WriteLine($"Found {usedStringOffsets.Count} used string offsets");

            // Step 2: Parse string heap to find all string ranges
            Console.WriteLine("Parsing string heap...");
            Dictionary<uint, uint> stringRanges = ParseStringHeap();
            Console.WriteLine($"Found {stringRanges.Count} strings in heap");

            // Step 3: Zero unused strings and calculate remaining bytes
            Console.WriteLine("Zeroing unused strings...");
            int trimmedStringCount = 0;
            int remainingStringCount = 0;
            long remainingBytes = 0;
            
            foreach (var (offset, length) in stringRanges)
            {
                if (!usedStringOffsets.Contains(offset))
                {
                    uint fileOffset = stringsStream.Offset + offset;
                    ZeroBytes(fileOffset, length);
                    trimmedStringCount++;
                }
                else
                {
                    remainingBytes += length;
                    remainingStringCount++;
                }
            }

            long trimmedBytes = _totalBytesZeroed - bytesZeroedBefore;
            Console.WriteLine($"Trimmed {trimmedStringCount} unused strings, kept {remainingStringCount} strings");
            Console.WriteLine($"#Strings heap: {originalSize:N0} bytes total, {trimmedBytes:N0} bytes trimmed ({(trimmedBytes * 100.0 / originalSize):F2}%), {remainingBytes:N0} bytes remaining ({(remainingBytes * 100.0 / originalSize):F2}%)");

            return (originalSize, trimmedBytes, remainingBytes);
        }

        /// <summary>
        /// 收集所有被保留元数据引用的字符串偏移
        /// </summary>
        private HashSet<uint> CollectUsedStringOffsets()
        {
            var usedOffsets = new HashSet<uint>();

            // Always keep offset 0 (empty string)
            usedOffsets.Add(0);

            // Collect from preserved TypeDef entries
            if (_metadata.TypeDefTable != null)
            {
                for (int i = 0; i < _metadata.TypeDefTable.Length; i++)
                {
                    // Skip trimmed types
                    if (i > 0 && ShouldTrimType(i))
                        continue;

                    var typeDef = _metadata.TypeDefTable[i];
                    usedOffsets.Add(typeDef.TypeName);
                    usedOffsets.Add(typeDef.TypeNamespace);
                }
            }

            // Collect from preserved MethodDef entries
            if (_metadata.MethodDefTable != null && _metadata.TypeDefTable != null)
            {
                for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
                {
                    // Skip trimmed types
                    if (typeIndex > 0 && ShouldTrimType(typeIndex))
                        continue;

                    var typeDef = _metadata.TypeDefTable[typeIndex];
                    string typeName = GetTypeName(typeDef);

                    uint methodStart = typeDef.MethodList;
                    uint methodEnd = typeIndex < _metadata.TypeDefTable.Length - 1
                        ? _metadata.TypeDefTable[typeIndex + 1].MethodList
                        : (uint)_metadata.MethodDefTable.Length + 1;

                    for (uint methodIdx = methodStart; methodIdx < methodEnd; methodIdx++)
                    {
                        if (methodIdx == 0 || methodIdx > _metadata.MethodDefTable.Length)
                            continue;

                        int methodIndex = (int)methodIdx - 1;
                        var method = _metadata.MethodDefTable[methodIndex];
                        string methodName = ReadString(method.Name);
                        string methodFullName = $"{typeName}.{methodName}";

                        // In class-level trimming, keep all method names of preserved types
                        // In method-level trimming, only keep names of preserved methods
                        if (!ShouldTrimMethod(methodFullName))
                        {
                            usedOffsets.Add(method.Name);
                        }
                    }
                }
            }

            // Collect from preserved Field entries
            if (_metadata.FieldTable != null && _metadata.TypeDefTable != null)
            {
                for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
                {
                    // Skip trimmed types
                    if (typeIndex > 0 && ShouldTrimType(typeIndex))
                        continue;

                    var typeDef = _metadata.TypeDefTable[typeIndex];

                    uint fieldStart = typeDef.FieldList;
                    uint fieldEnd = typeIndex < _metadata.TypeDefTable.Length - 1
                        ? _metadata.TypeDefTable[typeIndex + 1].FieldList
                        : (uint)(_metadata.FieldTable.Length) + 1;

                    for (uint fieldIdx = fieldStart; fieldIdx < fieldEnd; fieldIdx++)
                    {
                        if (fieldIdx == 0 || fieldIdx > _metadata.FieldTable.Length)
                            continue;

                        int fieldIndex = (int)fieldIdx - 1;
                        var field = _metadata.FieldTable[fieldIndex];
                        usedOffsets.Add(field.Name);
                    }
                }
            }

            // Collect from preserved Param entries
            if (_metadata.ParamTable != null && _metadata.MethodDefTable != null && _metadata.TypeDefTable != null)
            {
                for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
                {
                    // Skip trimmed types
                    if (typeIndex > 0 && ShouldTrimType(typeIndex))
                        continue;

                    var typeDef = _metadata.TypeDefTable[typeIndex];
                    string typeName = GetTypeName(typeDef);

                    uint methodStart = typeDef.MethodList;
                    uint methodEnd = typeIndex < _metadata.TypeDefTable.Length - 1
                        ? _metadata.TypeDefTable[typeIndex + 1].MethodList
                        : (uint)_metadata.MethodDefTable.Length + 1;

                    for (uint methodIdx = methodStart; methodIdx < methodEnd; methodIdx++)
                    {
                        if (methodIdx == 0 || methodIdx > _metadata.MethodDefTable.Length)
                            continue;

                        int methodIndex = (int)methodIdx - 1;
                        var method = _metadata.MethodDefTable[methodIndex];
                        string methodName = ReadString(method.Name);
                        string methodFullName = $"{typeName}.{methodName}";

                        // Skip trimmed methods
                        if (ShouldTrimMethod(methodFullName))
                            continue;

                        uint paramStart = method.ParamList;
                        uint paramEnd = methodIndex < _metadata.MethodDefTable.Length - 1
                            ? _metadata.MethodDefTable[methodIndex + 1].ParamList
                            : (uint)_metadata.ParamTable.Length + 1;

                        for (uint paramIdx = paramStart; paramIdx < paramEnd; paramIdx++)
                        {
                            if (paramIdx == 0 || paramIdx > _metadata.ParamTable.Length)
                                continue;

                            int paramIndex = (int)paramIdx - 1;
                            var param = _metadata.ParamTable[paramIndex];
                            usedOffsets.Add(param.Name);
                        }
                    }
                }
            }

            // Collect from shared tables (always preserved)
            // TypeRef
            if (_metadata.TypeRefTable != null)
            {
                foreach (var typeRef in _metadata.TypeRefTable)
                {
                    usedOffsets.Add(typeRef.TypeName);
                    usedOffsets.Add(typeRef.TypeNamespace);
                }
            }

            // MemberRef
            if (_metadata.MemberRefTable != null)
            {
                foreach (var memberRef in _metadata.MemberRefTable)
                {
                    usedOffsets.Add(memberRef.Name);
                }
            }

            // Module
            if (_metadata.ModuleTable != null)
            {
                foreach (var module in _metadata.ModuleTable)
                {
                    usedOffsets.Add(module.Name);
                }
            }

            // Assembly
            if (_metadata.AssemblyTable != null)
            {
                foreach (var assembly in _metadata.AssemblyTable)
                {
                    usedOffsets.Add(assembly.Name);
                    usedOffsets.Add(assembly.Culture);
                }
            }

            // AssemblyRef
            if (_metadata.AssemblyRefTable != null)
            {
                foreach (var assemblyRef in _metadata.AssemblyRefTable)
                {
                    usedOffsets.Add(assemblyRef.Name);
                    usedOffsets.Add(assemblyRef.Culture);
                }
            }

            // ModuleRef
            if (_metadata.ModuleRefTable != null)
            {
                foreach (var moduleRef in _metadata.ModuleRefTable)
                {
                    usedOffsets.Add(moduleRef.Name);
                }
            }

            // File
            if (_metadata.FileTable != null)
            {
                foreach (var file in _metadata.FileTable)
                {
                    usedOffsets.Add(file.Name);
                }
            }

            // Property
            if (_metadata.PropertyTable != null)
            {
                // Need to check if the property belongs to a preserved type
                // This requires PropertyMap table
                if (_metadata.PropertyMapTable != null && _metadata.TypeDefTable != null)
                {
                    foreach (var propMap in _metadata.PropertyMapTable)
                    {
                        int typeIndex = (int)propMap.Parent - 1;
                        if (typeIndex >= 0 && typeIndex < _metadata.TypeDefTable.Length)
                        {
                            // Skip trimmed types
                            if (typeIndex > 0 && ShouldTrimType(typeIndex))
                                continue;

                            // Find property range for this type
                            uint propStart = propMap.PropertyList;
                            uint propEnd = propStart + 1; // Simplified, should find actual range

                            for (uint propIdx = propStart; propIdx < propEnd && propIdx <= _metadata.PropertyTable.Length; propIdx++)
                            {
                                if (propIdx == 0)
                                    continue;

                                int propIndex = (int)propIdx - 1;
                                if (propIndex < _metadata.PropertyTable.Length)
                                {
                                    var prop = _metadata.PropertyTable[propIndex];
                                    usedOffsets.Add(prop.Name);
                                }
                            }
                        }
                    }
                }
            }

            // Event
            if (_metadata.EventTable != null)
            {
                // Similar to Property, need EventMap table
                if (_metadata.EventMapTable != null && _metadata.TypeDefTable != null)
                {
                    foreach (var eventMap in _metadata.EventMapTable)
                    {
                        int typeIndex = (int)eventMap.Parent - 1;
                        if (typeIndex >= 0 && typeIndex < _metadata.TypeDefTable.Length)
                        {
                            // Skip trimmed types
                            if (typeIndex > 0 && ShouldTrimType(typeIndex))
                                continue;

                            // Find event range for this type
                            uint eventStart = eventMap.EventList;
                            uint eventEnd = eventStart + 1; // Simplified

                            for (uint eventIdx = eventStart; eventIdx < eventEnd && eventIdx <= _metadata.EventTable.Length; eventIdx++)
                            {
                                if (eventIdx == 0)
                                    continue;

                                int evtIndex = (int)eventIdx - 1;
                                if (evtIndex < _metadata.EventTable.Length)
                                {
                                    var evt = _metadata.EventTable[evtIndex];
                                    usedOffsets.Add(evt.Name);
                                }
                            }
                        }
                    }
                }
            }

            return usedOffsets;
        }

        /// <summary>
        /// 解析#Strings堆，返回所有字符串的偏移和长度
        /// </summary>
        private Dictionary<uint, uint> ParseStringHeap()
        {
            var stringRanges = new Dictionary<uint, uint>();

            if (!_metadata.Streams.ContainsKey("#Strings"))
                return stringRanges;

            var stringsData = _metadata.Streams["#Strings"].Data;

            // Offset 0 is always empty string
            stringRanges[0] = 0;

            uint offset = 1; // Start from 1 (skip the initial null byte)
            while (offset < stringsData.Length)
            {
                uint start = offset;
                
                // Find the null terminator
                while (offset < stringsData.Length && stringsData[offset] != 0)
                {
                    offset++;
                }

                // Calculate string length (including null terminator)
                uint length = offset - start + 1;
                
                if (length > 1) // Non-empty string
                {
                    stringRanges[start] = length;
                }

                offset++; // Move past the null terminator
            }

            return stringRanges;
        }

        #endregion

        /// <summary>
        /// 保存剪裁后的文件
        /// </summary>
        public void SaveTrimmedFile(string outputPath)
        {
            Console.WriteLine($"\nSaving trimmed file to: {outputPath}");
            File.WriteAllBytes(outputPath, _fileData);
            Console.WriteLine("File saved successfully");
        }
    }
}
