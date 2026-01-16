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

            // Build a method index for fast lookup: methodFullName -> MethodDef
            // This avoids the O(n*m*k) triple nested loop
            if (_metadata.MethodDefTable != null)
            {
                Console.WriteLine("Building method index...");
                var methodIndex = new Dictionary<string, MethodDefRow>(StringComparer.OrdinalIgnoreCase);
                
                for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
                {
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

                        int methodDefIndex = (int)methodIdx - 1;
                        var method = _metadata.MethodDefTable[methodDefIndex];
                        string methodName = ReadString(method.Name);
                        string fullName = $"{typeName}.{methodName}";
                        string normalizedFullName = NormalizeMethodNameForComparison(fullName);
                        
                        // Store both normalized and original names
                        methodIndex[normalizedFullName] = method;
                        if (normalizedFullName != fullName)
                        {
                            methodIndex[fullName] = method;
                        }
                    }
                }
                
                Console.WriteLine($"Built index for {methodIndex.Count} methods");

                // Now extract types from method signatures using the index
                int signaturesAnalyzed = 0;
                foreach (var methodFullName in _invokedMethods)
                {
                    if (methodIndex.TryGetValue(methodFullName, out var method))
                    {
                        if (method.ParsedSignature != null)
                        {
                            ExtractTypesFromSignature(method.ParsedSignature, invokedTypeNames);
                            signaturesAnalyzed++;
                        }
                    }
                }
                
                Console.WriteLine($"Analyzed {signaturesAnalyzed} method signatures");
            }

            Console.WriteLine($"After signature analysis: {invokedTypeNames.Count} unique type names");

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
        /// 从方法签名中提取类型名称
        /// </summary>
        private void ExtractTypesFromSignature(MethodSignature signature, HashSet<string> typeNames)
        {
            // Extract return type
            ExtractTypeFromSignatureType(signature.ReturnType, typeNames);

            // Extract parameter types
            foreach (var param in signature.Parameters)
            {
                ExtractTypeFromSignatureType(param, typeNames);
            }
        }

        /// <summary>
        /// 从签名类型中提取类型名称
        /// </summary>
        private void ExtractTypeFromSignatureType(SignatureType sigType, HashSet<string> typeNames)
        {
            if (sigType == null)
                return;

            switch (sigType.ElementType)
            {
                case ElementType.VALUETYPE:
                case ElementType.CLASS:
                    // Token format: 0x02000000 (TypeDef) or 0x01000000 (TypeRef)
                    string? typeName = ResolveTypeNameFromToken(sigType.Token);
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        typeNames.Add(typeName);
                    }
                    break;

                case ElementType.PTR:
                case ElementType.BYREF:
                case ElementType.SZARRAY:
                case ElementType.PINNED:
                    // Recursively extract from inner type
                    if (sigType.InnerType != null)
                    {
                        ExtractTypeFromSignatureType(sigType.InnerType, typeNames);
                    }
                    break;

                case ElementType.GENERICINST:
                    // Extract from generic type and its arguments
                    if (sigType.InnerType != null)
                    {
                        ExtractTypeFromSignatureType(sigType.InnerType, typeNames);
                    }
                    if (sigType.GenericArgs != null)
                    {
                        foreach (var arg in sigType.GenericArgs)
                        {
                            ExtractTypeFromSignatureType(arg, typeNames);
                        }
                    }
                    break;

                case ElementType.ARRAY:
                    // Extract from array element type
                    if (sigType.InnerType != null)
                    {
                        ExtractTypeFromSignatureType(sigType.InnerType, typeNames);
                    }
                    break;

                // Primitive types don't need to be preserved
                default:
                    break;
            }
        }

        /// <summary>
        /// 从token解析类型名称
        /// </summary>
        private string? ResolveTypeNameFromToken(uint token)
        {
            // The token in signature is a TypeDefOrRef coded index, need to decode it
            // TypeDefOrRef encoding: 2 bits for tag, remaining bits for index
            // Tag: 0 = TypeDef, 1 = TypeRef, 2 = TypeSpec
            int tag = (int)(token & 0x03);
            int index = (int)(token >> 2) - 1; // Subtract 1 because table indices are 1-based

            if (tag == 0) // TypeDef
            {
                if (_metadata.TypeDefTable != null && index >= 0 && index < _metadata.TypeDefTable.Length)
                {
                    return GetTypeName(_metadata.TypeDefTable[index]);
                }
            }
            else if (tag == 1) // TypeRef
            {
                if (_metadata.TypeRefTable != null && index >= 0 && index < _metadata.TypeRefTable.Length)
                {
                    var typeRef = _metadata.TypeRefTable[index];
                    string ns = ReadString(typeRef.TypeNamespace);
                    string name = ReadString(typeRef.TypeName);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
            }
            else if (tag == 2) // TypeSpec
            {
                // TypeSpec references are more complex, would need to parse the blob
                // For now, skip TypeSpec
            }

            return null;
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
        public bool ShouldTrimType(int typeIndex)
        {
            // Check if the type is in the invoked types set
            if (_invokedTypes.Contains(typeIndex))
                return false;

            // Always preserve compiler-generated types
            if (_metadata.TypeDefTable != null && typeIndex >= 0 && typeIndex < _metadata.TypeDefTable.Length)
            {
                var typeDef = _metadata.TypeDefTable[typeIndex];
                string typeName = ReadString(typeDef.TypeName);
                
                // Preserve <PrivateImplementationDetails> and its nested types
                // These are used by the compiler to store static array initialization data, literals, etc.
                if (typeName.StartsWith("<PrivateImplementationDetails>") || 
                    typeName.StartsWith("__StaticArrayInitTypeSize="))
                {
                    return false;
                }
                
                // Check if this is a nested type of <PrivateImplementationDetails>
                // by checking the EnclosingClass in NestedClass table
                if (_metadata.NestedClassTable != null)
                {
                    foreach (var nested in _metadata.NestedClassTable)
                    {
                        if (nested.NestedClass == typeIndex + 1) // Table indices are 1-based
                        {
                            int enclosingIndex = (int)nested.EnclosingClass - 1;
                            if (enclosingIndex >= 0 && enclosingIndex < _metadata.TypeDefTable.Length)
                            {
                                var enclosingType = _metadata.TypeDefTable[enclosingIndex];
                                string enclosingName = ReadString(enclosingType.TypeName);
                                if (enclosingName.StartsWith("<PrivateImplementationDetails>"))
                                {
                                    return false;
                                }
                            }
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 判断方法是否应该被剪裁（方法级别剪裁）
        /// </summary>
        public bool ShouldTrimMethod(string methodFullName)
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
                                
                                // Calculate total size including alignment padding after exception table
                                uint endOffset = sectOffset + dataSize;
                                // Align to 4 bytes for next method
                                endOffset = (endOffset + 3) & ~3u;
                                totalSize = endOffset - offset;
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

        public uint RVAToFileOffset(uint rva)
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

        public string GetTypeName(TypeDefRow typeDef)
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

        public string ReadString(uint offset)
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

        #region S2 Trimming - IL Token Extractor

        /// <summary>
        /// IL Token 提取器 - 从方法体中提取所有 Token 引用
        /// </summary>
        private class ILTokenExtractor
        {
            private readonly byte[] _ilCode;
            private readonly HashSet<uint> _extractedTokens;

            // IL 操作码定义（包含 Token 的指令）
            private static readonly HashSet<byte> _oneByteTokenOpcodes = new HashSet<byte>
            {
                0x28, // call
                0x6F, // callvirt
                0x73, // newobj
                0x7B, // ldfld
                0x7C, // ldflda
                0x7D, // stfld
                0x7E, // ldsfld
                0x7F, // ldsflda
                0x80, // stsfld
                0xD0, // ldtoken
                0x74, // castclass
                0x75, // isinst
                0x8C, // box
                0x79, // unbox
                0x8D, // newarr
                0x8F, // ldelema
                0xC2, // refanyval
                0xC6, // mkrefany
            };

            private static readonly HashSet<ushort> _twoByteTokenOpcodes = new HashSet<ushort>
            {
                0xFE06, // ldftn
                0xFE07, // ldvirtftn
                0xFE09, // initobj
                0xFE0C, // constrained.
                0xFE15, // initblk
                0xFE1C, // sizeof
            };

            public ILTokenExtractor(byte[] ilCode)
            {
                _ilCode = ilCode;
                _extractedTokens = new HashSet<uint>();
            }

            /// <summary>
            /// 提取 IL 代码中的所有 Token
            /// </summary>
            public HashSet<uint> ExtractTokens()
            {
                int pos = 0;
                int lastPos = -1;
                int stuckCount = 0;
                
                while (pos < _ilCode.Length)
                {
                    // 检测死循环
                    if (pos == lastPos)
                    {
                        stuckCount++;
                        if (stuckCount > 10)
                        {
                            // 强制前进，避免死循环
                            pos++;
                            stuckCount = 0;
                            continue;
                        }
                    }
                    else
                    {
                        lastPos = pos;
                        stuckCount = 0;
                    }
                    
                    try
                    {
                        byte opcode = _ilCode[pos];
                        int oldPos = pos;
                        
                        // 检查是否是双字节操作码
                        if (opcode == 0xFE && pos + 1 < _ilCode.Length)
                        {
                            byte secondByte = _ilCode[pos + 1];
                            ushort twoByteOpcode = (ushort)((opcode << 8) | secondByte);
                            
                            if (_twoByteTokenOpcodes.Contains(twoByteOpcode))
                            {
                                // 读取 Token（4 字节）
                                if (pos + 6 <= _ilCode.Length)
                                {
                                    uint token = BitConverter.ToUInt32(_ilCode, pos + 2);
                                    _extractedTokens.Add(token);
                                    pos += 6; // 2 (opcode) + 4 (token)
                                }
                                else
                                {
                                    break;
                                }
                            }
                            else
                            {
                                // 其他双字节操作码
                                int size = GetTwoByteOpcodeSize(twoByteOpcode);
                                if (size <= 0) size = 2; // 安全默认值
                                pos += size;
                            }
                        }
                        else if (_oneByteTokenOpcodes.Contains(opcode))
                        {
                            // 读取 Token（4 字节）
                            if (pos + 5 <= _ilCode.Length)
                            {
                                uint token = BitConverter.ToUInt32(_ilCode, pos + 1);
                                _extractedTokens.Add(token);
                                pos += 5; // 1 (opcode) + 4 (token)
                            }
                            else
                            {
                                break;
                            }
                        }
                        else if (opcode == 0x29) // calli
                        {
                            // calli 使用 StandAloneSig token
                            if (pos + 5 <= _ilCode.Length)
                            {
                                uint token = BitConverter.ToUInt32(_ilCode, pos + 1);
                                _extractedTokens.Add(token);
                                pos += 5;
                            }
                            else
                            {
                                break;
                            }
                        }
                        else if (opcode == 0x21) // ldstr
                        {
                            // ldstr 使用 String token
                            if (pos + 5 <= _ilCode.Length)
                            {
                                uint token = BitConverter.ToUInt32(_ilCode, pos + 1);
                                _extractedTokens.Add(token);
                                pos += 5;
                            }
                            else
                            {
                                break;
                            }
                        }
                        else if (opcode == 0x45) // switch
                        {
                            // switch 指令：switch <uint32 N> <int32 target1> ... <int32 targetN>
                            if (pos + 5 <= _ilCode.Length)
                            {
                                uint n = BitConverter.ToUInt32(_ilCode, pos + 1);
                                // 限制 n 的大小，防止异常值
                                if (n > 10000) n = 0;
                                pos += 5 + (int)(n * 4); // 1 (opcode) + 4 (N) + N*4 (targets)
                            }
                            else
                            {
                                break;
                            }
                        }
                        else
                        {
                            // 其他单字节操作码
                            int size = GetOneByteOpcodeSize(opcode);
                            if (size <= 0) size = 1; // 安全默认值
                            pos += size;
                        }
                        
                        // 确保位置有前进
                        if (pos <= oldPos)
                        {
                            pos = oldPos + 1;
                        }
                    }
                    catch (Exception)
                    {
                        // 遇到无效操作码，跳过
                        pos++;
                    }
                }

                return _extractedTokens;
            }

            /// <summary>
            /// 获取单字节操作码的大小
            /// </summary>
            private int GetOneByteOpcodeSize(byte opcode)
            {
                // 根据 ECMA-335 规范返回操作码大小
                switch (opcode)
                {
                    // 无操作数
                    case 0x00: case 0x01: case 0x02: case 0x03: case 0x04: case 0x05: case 0x06: case 0x07:
                    case 0x08: case 0x09: case 0x0A: case 0x0B: case 0x0C: case 0x0D: case 0x0E:
                    case 0x14: case 0x15: case 0x16: case 0x17: case 0x18: case 0x19: case 0x1A: case 0x1B:
                    case 0x1C: case 0x1D: case 0x1E:
                    case 0x25: case 0x26: case 0x27: case 0x2A:
                    case 0x46: case 0x47: case 0x48: case 0x49: case 0x4A:
                    case 0x4B: case 0x4C: case 0x4D: case 0x4E: case 0x4F: case 0x50: case 0x51: case 0x52:
                    case 0x53: case 0x54: case 0x55: case 0x56: case 0x57: case 0x58: case 0x59: case 0x5A:
                    case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F: case 0x60: case 0x61: case 0x62:
                    case 0x63: case 0x64: case 0x65: case 0x66: case 0x67: case 0x68: case 0x69: case 0x6A:
                    case 0x6B: case 0x6C: case 0x6D: case 0x6E: case 0x76: case 0x77: case 0x78: case 0x7A:
                    case 0x81: case 0x82: case 0x83: case 0x84: case 0x85: case 0x86: case 0x87: case 0x88:
                    case 0x89: case 0x8A: case 0x8B: case 0x8E: case 0x90: case 0x91: case 0x92: case 0x93:
                    case 0x94: case 0x95: case 0x96: case 0x97: case 0x98: case 0x99: case 0x9A: case 0x9B:
                    case 0x9C: case 0x9D: case 0x9E: case 0x9F: case 0xA0: case 0xA1: case 0xA2: case 0xA3:
                    case 0xA4: case 0xA5: case 0xB3: case 0xC3: case 0xC4: case 0xC5: case 0xD1: case 0xD2:
                    case 0xD3: case 0xDC:
                        return 1;

                    // 1 字节操作数
                    case 0x0F: case 0x10: case 0x11: case 0x12: case 0x13: // ldarg, ldarga, starg, ldloc, ldloca
                    case 0x1F: case 0x20: // ldc.i4.s
                        return 2;

                    // 2 字节操作数（短分支指令）
                    case 0x2B: case 0x2C: case 0x2D: case 0x2E: case 0x2F: case 0x30: case 0x31: case 0x32: // br.s, brfalse.s, brtrue.s, beq.s, bge.s, bgt.s, ble.s, blt.s
                    case 0x33: case 0x34: case 0x35: case 0x36: case 0x37: // bne.un.s, bge.un.s, bgt.un.s, ble.un.s, blt.un.s
                    case 0xDE: // leave.s
                        return 2;

                    // 5 字节操作数（4字节操作数的指令）
                    case 0x21: // ldstr (string token)
                    case 0x22: // ldc.i4
                    case 0x23: // ldc.r4
                    case 0x28: // call (method token) - 已在 ExtractTokens 中处理
                    case 0x29: // calli (signature token) - 已在 ExtractTokens 中处理
                    case 0x38: case 0x39: case 0x3A: case 0x3B: case 0x3C: case 0x3D: case 0x3E: case 0x3F: // br, beq, bge, bgt, ble, blt, bne.un, bge.un
                    case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: // bgt.un, ble.un, blt.un, brfalse, brtrue
                    case 0x6F: // callvirt (method token) - 已在 ExtractTokens 中处理
                    case 0x73: // newobj (method token) - 已在 ExtractTokens 中处理
                    case 0x74: // castclass (type token) - 已在 ExtractTokens 中处理
                    case 0x75: // isinst (type token) - 已在 ExtractTokens 中处理
                    case 0x79: // unbox (type token) - 已在 ExtractTokens 中处理
                    case 0x7B: // ldfld (field token) - 已在 ExtractTokens 中处理
                    case 0x7C: // ldflda (field token) - 已在 ExtractTokens 中处理
                    case 0x7D: // stfld (field token) - 已在 ExtractTokens 中处理
                    case 0x7E: // ldsfld (field token) - 已在 ExtractTokens 中处理
                    case 0x7F: // ldsflda (field token) - 已在 ExtractTokens 中处理
                    case 0x80: // stsfld (field token) - 已在 ExtractTokens 中处理
                    case 0x8C: // box (type token) - 已在 ExtractTokens 中处理
                    case 0x8D: // newarr (type token) - 已在 ExtractTokens 中处理
                    case 0x8F: // ldelema (type token) - 已在 ExtractTokens 中处理
                    case 0xC2: // refanyval (type token) - 已在 ExtractTokens 中处理
                    case 0xC6: // mkrefany (type token) - 已在 ExtractTokens 中处理
                    case 0xD0: // ldtoken (token) - 已在 ExtractTokens 中处理
                    case 0xDD: // leave
                        return 5;

                    // 9 字节操作数（8字节操作数的指令）
                    case 0x24: // ldc.r8
                        return 9;

                    // 默认：1 字节（保守估计）
                    default:
                        return 1;
                }
            }

            /// <summary>
            /// 获取双字节操作码的大小
            /// </summary>
            private int GetTwoByteOpcodeSize(ushort opcode)
            {
                // 大多数双字节操作码都有 4 字节操作数或无操作数
                switch (opcode)
                {
                    case 0xFE00: case 0xFE01: case 0xFE02: case 0xFE03: case 0xFE04: case 0xFE05:
                    case 0xFE08: case 0xFE0A: case 0xFE0B: case 0xFE0D: case 0xFE0E: case 0xFE0F:
                    case 0xFE10: case 0xFE11: case 0xFE12: case 0xFE13: case 0xFE14: case 0xFE16:
                    case 0xFE17: case 0xFE18: case 0xFE19: case 0xFE1A: case 0xFE1B: case 0xFE1D:
                    case 0xFE1E:
                        return 2; // 无操作数

                    default:
                        return 6; // 2 (opcode) + 4 (operand)
                }
            }
        }

        #endregion

        #region S2 Trimming - Metadata Reference Analyzer

        /// <summary>
        /// 元数据引用关系分析器 - 构建引用关系图并标记使用中的元数据
        /// </summary>
        private class MetadataReferenceAnalyzer
        {
            private readonly MetadataRoot _metadata;
            private readonly byte[] _fileData;
            private readonly HashSet<uint> _usedTokens;
            private readonly PETrimmer _trimmer;

            public MetadataReferenceAnalyzer(MetadataRoot metadata, byte[] fileData, PETrimmer trimmer)
            {
                _metadata = metadata;
                _fileData = fileData;
                _usedTokens = new HashSet<uint>();
                _trimmer = trimmer;
            }

            /// <summary>
            /// 构建引用关系图并标记所有使用中的元数据
            /// </summary>
            public HashSet<uint> BuildReferenceGraph()
            {
                Console.WriteLine("Building metadata reference graph...");

                // 1. 扫描保留的 TypeDef 表
                Console.Write("  Scanning TypeDef table...");
                ScanPreservedTypeDefs();
                Console.WriteLine($" {_usedTokens.Count} tokens");

                // 2. 扫描保留的 MethodDef 表
                Console.Write("  Scanning MethodDef table...");
                int beforeMethods = _usedTokens.Count;
                ScanPreservedMethodDefs();
                Console.WriteLine($" +{_usedTokens.Count - beforeMethods} tokens");

                // 3. 扫描保留的 Field 表
                Console.Write("  Scanning Field table...");
                int beforeFields = _usedTokens.Count;
                ScanPreservedFields();
                Console.WriteLine($" +{_usedTokens.Count - beforeFields} tokens");

                // 4. 递归标记所有被引用的元数据
                Console.Write("  Marking referenced metadata...");
                MarkReferencedMetadata();
                Console.WriteLine(" done");

                // 5. 扫描 CustomAttribute 表（在所有引用标记完成后）
                Console.Write("  Scanning CustomAttribute table...");
                int beforeAttrs = _usedTokens.Count;
                ScanCustomAttributes();
                Console.WriteLine($" +{_usedTokens.Count - beforeAttrs} tokens");

                Console.WriteLine($"Found {_usedTokens.Count} used metadata tokens");
                return _usedTokens;
            }

            private void ScanPreservedTypeDefs()
            {
                if (_metadata.TypeDefTable == null) return;

                for (int i = 0; i < _metadata.TypeDefTable.Length; i++)
                {
                    // 跳过被剪裁的类型
                    if (i > 0 && _trimmer.ShouldTrimType(i))
                        continue;

                    var typeDef = _metadata.TypeDefTable[i];
                    uint token = (uint)(0x02000000 | (i + 1)); // TypeDef token
                    _usedTokens.Add(token);

                    // 标记 Extends (基类)
                    if (typeDef.Extends != 0)
                    {
                        _usedTokens.Add(DecodeTypeDefOrRef(typeDef.Extends));
                    }

                    // 标记 InterfaceImpl
                    if (_metadata.InterfaceImplTable != null)
                    {
                        foreach (var impl in _metadata.InterfaceImplTable)
                        {
                            if (impl.Class == i + 1)
                            {
                                _usedTokens.Add(DecodeTypeDefOrRef(impl.Interface));
                            }
                        }
                    }
                }
            }

            private void ScanPreservedMethodDefs()
            {
                if (_metadata.MethodDefTable == null || _metadata.TypeDefTable == null) return;

                int processedMethods = 0;
                int totalMethods = 0;
                
                // 先计算总数
                for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
                {
                    if (typeIndex > 0 && _trimmer.ShouldTrimType(typeIndex))
                        continue;

                    var typeDef = _metadata.TypeDefTable[typeIndex];
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
                        string methodName = _trimmer.ReadString(method.Name);
                        string typeName = _trimmer.GetTypeName(typeDef);
                        string methodFullName = $"{typeName}.{methodName}";

                        if (!_trimmer.ShouldTrimMethod(methodFullName))
                            totalMethods++;
                    }
                }

                Console.Write($" (0/{totalMethods})");

                for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
                {
                    // 跳过被剪裁的类型
                    if (typeIndex > 0 && _trimmer.ShouldTrimType(typeIndex))
                        continue;

                    var typeDef = _metadata.TypeDefTable[typeIndex];
                    string typeName = _trimmer.GetTypeName(typeDef);

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
                        string methodName = _trimmer.ReadString(method.Name);
                        string methodFullName = $"{typeName}.{methodName}";

                        // 跳过被剪裁的方法
                        if (_trimmer.ShouldTrimMethod(methodFullName))
                            continue;

                        uint token = (uint)(0x06000000 | methodIdx); // MethodDef token
                        _usedTokens.Add(token);

                        // 标记方法签名
                        if (method.Signature != 0)
                        {
                            MarkBlobSignature(method.Signature);
                        }

                        // 解析方法体中的 Token
                        if (method.RVA != 0)
                        {
                            ExtractTokensFromMethodBody(method.RVA);
                        }

                        processedMethods++;
                        if (processedMethods % 500 == 0)
                        {
                            Console.Write($"\r  Scanning MethodDef table... ({processedMethods}/{totalMethods})");
                        }
                    }
                }
            }

            private void ScanPreservedFields()
            {
                if (_metadata.FieldTable == null || _metadata.TypeDefTable == null) return;

                for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
                {
                    // 跳过被剪裁的类型
                    if (typeIndex > 0 && _trimmer.ShouldTrimType(typeIndex))
                        continue;

                    var typeDef = _metadata.TypeDefTable[typeIndex];

                    uint fieldStart = typeDef.FieldList;
                    uint fieldEnd = typeIndex < _metadata.TypeDefTable.Length - 1
                        ? _metadata.TypeDefTable[typeIndex + 1].FieldList
                        : (uint)_metadata.FieldTable.Length + 1;

                    for (uint fieldIdx = fieldStart; fieldIdx < fieldEnd; fieldIdx++)
                    {
                        if (fieldIdx == 0 || fieldIdx > _metadata.FieldTable.Length)
                            continue;

                        int fieldIndex = (int)fieldIdx - 1;
                        var field = _metadata.FieldTable[fieldIndex];

                        uint token = (uint)(0x04000000 | fieldIdx); // Field token
                        _usedTokens.Add(token);

                        // 标记字段签名
                        if (field.Signature != 0)
                        {
                            MarkBlobSignature(field.Signature);
                        }

                        // 标记 Constant
                        if (_metadata.ConstantTable != null)
                        {
                            foreach (var constant in _metadata.ConstantTable)
                            {
                                uint parentToken = DecodeHasConstant(constant.Parent);
                                if (parentToken == token)
                                {
                                    // 标记常量值
                                    if (constant.Value != 0)
                                    {
                                        MarkBlobData(constant.Value);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            private void ScanCustomAttributes()
            {
                if (_metadata.CustomAttributeTable == null) return;

                foreach (var attr in _metadata.CustomAttributeTable)
                {
                    uint parentToken = DecodeHasCustomAttribute(attr.Parent);
                    
                    // 检查父元素是否被保留
                    if (_usedTokens.Contains(parentToken))
                    {
                        // 标记 CustomAttribute 的 Type (构造函数)
                        uint typeToken = DecodeCustomAttributeType(attr.Type);
                        _usedTokens.Add(typeToken);

                        // 标记 CustomAttribute 的 Value
                        if (attr.Value != 0)
                        {
                            MarkBlobData(attr.Value);
                        }
                    }
                }
            }

            private void MarkReferencedMetadata()
            {
                // 递归标记所有被引用的元数据
                bool changed = true;
                int iterations = 0;
                
                while (changed && iterations < 100) // 防止无限循环
                {
                    changed = false;
                    int beforeCount = _usedTokens.Count;

                    // 处理 MemberRef
                    if (_metadata.MemberRefTable != null)
                    {
                        for (int i = 0; i < _metadata.MemberRefTable.Length; i++)
                        {
                            uint token = (uint)(0x0A000000 | (i + 1));
                            if (_usedTokens.Contains(token))
                            {
                                var memberRef = _metadata.MemberRefTable[i];
                                
                                // 标记 Class
                                uint classToken = DecodeMemberRefParent(memberRef.Class);
                                if (classToken != 0)
                                    _usedTokens.Add(classToken);

                                // 标记 Signature
                                if (memberRef.Signature != 0)
                                {
                                    MarkBlobSignature(memberRef.Signature);
                                }
                            }
                        }
                    }

                    // 处理 TypeSpec
                    if (_metadata.TypeSpecTable != null)
                    {
                        for (int i = 0; i < _metadata.TypeSpecTable.Length; i++)
                        {
                            uint token = (uint)(0x1B000000 | (i + 1));
                            if (_usedTokens.Contains(token))
                            {
                                var typeSpec = _metadata.TypeSpecTable[i];
                                
                                // 标记 Signature
                                if (typeSpec.Signature != 0)
                                {
                                    MarkBlobSignature(typeSpec.Signature);
                                }
                            }
                        }
                    }

                    // 处理 MethodSpec
                    if (_metadata.MethodSpecTable != null)
                    {
                        for (int i = 0; i < _metadata.MethodSpecTable.Length; i++)
                        {
                            uint token = (uint)(0x2B000000 | (i + 1));
                            if (_usedTokens.Contains(token))
                            {
                                var methodSpec = _metadata.MethodSpecTable[i];
                                
                                // 标记 Method
                                uint methodToken = DecodeMethodDefOrRef(methodSpec.Method);
                                if (methodToken != 0)
                                    _usedTokens.Add(methodToken);

                                // 标记 Instantiation
                                if (methodSpec.Instantiation != 0)
                                {
                                    MarkBlobSignature(methodSpec.Instantiation);
                                }
                            }
                        }
                    }

                    int addedCount = _usedTokens.Count - beforeCount;
                    if (addedCount > 0)
                    {
                        changed = true;
                        Console.Write($".");
                    }

                    iterations++;
                }

                if (iterations > 0)
                    Console.WriteLine($" ({iterations} iterations)");
            }

            private void ExtractTokensFromMethodBody(uint rva)
            {
                try
                {
                    uint offset = _trimmer.RVAToFileOffset(rva);
                    if (offset == 0 || offset >= _fileData.Length)
                        return;

                    byte firstByte = _fileData[offset];
                    byte[] ilCode;

                    // 检查方法体格式
                    if ((firstByte & 0x03) == 0x02) // Tiny format
                    {
                        uint codeSize = (uint)(firstByte >> 2);
                        ilCode = new byte[codeSize];
                        Array.Copy(_fileData, offset + 1, ilCode, 0, codeSize);
                    }
                    else if ((firstByte & 0x03) == 0x03) // Fat format
                    {
                        uint codeSize = BitConverter.ToUInt32(_fileData, (int)offset + 4);
                        ilCode = new byte[codeSize];
                        Array.Copy(_fileData, offset + 12, ilCode, 0, codeSize);

                        // 处理 LocalVarSigTok
                        ushort flags = BitConverter.ToUInt16(_fileData, (int)offset);
                        if ((flags & 0x10) != 0) // InitLocals flag
                        {
                            uint localVarSigTok = BitConverter.ToUInt32(_fileData, (int)offset + 8);
                            if (localVarSigTok != 0)
                            {
                                _usedTokens.Add(localVarSigTok);
                            }
                        }
                    }
                    else
                    {
                        return;
                    }

                    // 提取 IL 代码中的 Token
                    var extractor = new ILTokenExtractor(ilCode);
                    var tokens = extractor.ExtractTokens();
                    
                    foreach (var token in tokens)
                    {
                        _usedTokens.Add(token);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to extract tokens from method body at RVA 0x{rva:X}: {ex.Message}");
                }
            }

            private void MarkBlobSignature(uint blobOffset)
            {
                // 简化处理：标记 Blob 偏移
                // 实际应该解析签名并提取其中的类型引用
                MarkBlobData(blobOffset);
            }

            private void MarkBlobData(uint blobOffset)
            {
                // 标记 Blob 数据（通过特殊的伪 Token）
                // 使用高位标记：0x70000000 | blobOffset
                _usedTokens.Add(0x70000000 | blobOffset);
            }

            // Coded Index 解码方法
            private uint DecodeTypeDefOrRef(uint codedIndex)
            {
                int tag = (int)(codedIndex & 0x03);
                int index = (int)(codedIndex >> 2);
                
                switch (tag)
                {
                    case 0: return (uint)(0x02000000 | index); // TypeDef
                    case 1: return (uint)(0x01000000 | index); // TypeRef
                    case 2: return (uint)(0x1B000000 | index); // TypeSpec
                    default: return 0;
                }
            }

            private uint DecodeMemberRefParent(uint codedIndex)
            {
                int tag = (int)(codedIndex & 0x07);
                int index = (int)(codedIndex >> 3);
                
                switch (tag)
                {
                    case 0: return (uint)(0x02000000 | index); // TypeDef
                    case 1: return (uint)(0x01000000 | index); // TypeRef
                    case 2: return (uint)(0x1A000000 | index); // ModuleRef
                    case 3: return (uint)(0x06000000 | index); // MethodDef
                    case 4: return (uint)(0x1B000000 | index); // TypeSpec
                    default: return 0;
                }
            }

            private uint DecodeMethodDefOrRef(uint codedIndex)
            {
                int tag = (int)(codedIndex & 0x01);
                int index = (int)(codedIndex >> 1);
                
                switch (tag)
                {
                    case 0: return (uint)(0x06000000 | index); // MethodDef
                    case 1: return (uint)(0x0A000000 | index); // MemberRef
                    default: return 0;
                }
            }

            private uint DecodeHasConstant(uint codedIndex)
            {
                int tag = (int)(codedIndex & 0x03);
                int index = (int)(codedIndex >> 2);
                
                switch (tag)
                {
                    case 0: return (uint)(0x04000000 | index); // Field
                    case 1: return (uint)(0x08000000 | index); // Param
                    case 2: return (uint)(0x17000000 | index); // Property
                    default: return 0;
                }
            }

            private uint DecodeHasCustomAttribute(uint codedIndex)
            {
                int tag = (int)(codedIndex & 0x1F);
                int index = (int)(codedIndex >> 5);
                
                // 简化处理，只处理常见的表
                switch (tag)
                {
                    case 0: return (uint)(0x06000000 | index); // MethodDef
                    case 1: return (uint)(0x04000000 | index); // Field
                    case 2: return (uint)(0x01000000 | index); // TypeRef
                    case 3: return (uint)(0x02000000 | index); // TypeDef
                    case 4: return (uint)(0x08000000 | index); // Param
                    case 6: return (uint)(0x0A000000 | index); // MemberRef
                    case 9: return (uint)(0x17000000 | index); // Property
                    case 10: return (uint)(0x14000000 | index); // Event
                    case 13: return (uint)(0x20000000 | index); // Assembly
                    default: return 0;
                }
            }

            private uint DecodeCustomAttributeType(uint codedIndex)
            {
                int tag = (int)(codedIndex & 0x07);
                int index = (int)(codedIndex >> 3);
                
                switch (tag)
                {
                    case 2: return (uint)(0x06000000 | index); // MethodDef
                    case 3: return (uint)(0x0A000000 | index); // MemberRef
                    default: return 0;
                }
            }
        }

        #endregion

        #region Deep Trimming - Public Methods

        // Deep 剪裁统计变量
        private long _deepTrimmedBytes;
        private Dictionary<string, (int total, int trimmed, long trimmedBytes)> _deepTableStats = new Dictionary<string, (int, int, long)>();
        private long _deepTrimmedBlobBytes;
        private long _deepTrimmedUSBytes;

        /// <summary>
        /// 执行 Deep 级别元数据剪裁（必须在 S0/S1 剪裁后执行）
        /// </summary>
        public void TrimAtDeepLevel()
        {
            Console.WriteLine("\n=== Starting Deep-Level Metadata Trimming ===");
            Console.WriteLine("Note: Deep trimming must be performed after S0/S1 trimming");

            _deepTrimmedBytes = 0;
            _deepTableStats.Clear();

            // 1. 构建引用关系图
            var analyzer = new MetadataReferenceAnalyzer(_metadata, _fileData, this);
            var usedTokens = analyzer.BuildReferenceGraph();

            // 2. 剪裁未使用的元数据表行
            TrimUnusedMetadataTables(usedTokens);

            // 3. 剪裁未使用的 Blob 堆数据
            TrimUnusedBlobData(usedTokens);

            // 4. 剪裁未使用的 #US 堆数据
            TrimUnusedUserStrings(usedTokens);

            // 5. 输出统计信息
            PrintDeepStatistics();

            Console.WriteLine("\n=== Deep Trimming Complete ===");
        }

        /// <summary>
        /// 剪裁未使用的元数据表行
        /// </summary>
        private void TrimUnusedMetadataTables(HashSet<uint> usedTokens)
        {
            Console.WriteLine("\n=== Trimming Unused Metadata Tables ===");

            // 剪裁 TypeRef 表
            TrimTable("TypeRef", 0x01, _metadata.TypeRefTable?.Length ?? 0, usedTokens, 
                i => 6 + _metadata.StringIndexSize * 2); // ResolutionScope + TypeName + TypeNamespace

            // 剪裁 MemberRef 表
            TrimTable("MemberRef", 0x0A, _metadata.MemberRefTable?.Length ?? 0, usedTokens,
                i => GetCodedIndexSize(new[] { 0x02, 0x01, 0x1A, 0x06, 0x1B }) + _metadata.StringIndexSize + _metadata.BlobIndexSize);

            // 剪裁 Constant 表
            if (_metadata.ConstantTable != null)
            {
                int trimmedCount = 0;
                long trimmedBytes = 0;
                
                for (int i = 0; i < _metadata.ConstantTable.Length; i++)
                {
                    var constant = _metadata.ConstantTable[i];
                    uint parentToken = DecodeHasConstant(constant.Parent);
                    
                    if (!usedTokens.Contains(parentToken))
                    {
                        uint rowOffset = GetTableRowOffset(0x0B, i);
                        if (rowOffset > 0)
                        {
                            int rowSize = 2 + GetCodedIndexSize(new[] { 0x04, 0x08, 0x17 }) + _metadata.BlobIndexSize;
                            ZeroBytes(rowOffset, (uint)rowSize);
                            trimmedCount++;
                            trimmedBytes += rowSize;
                        }
                    }
                }
                
                _deepTableStats["Constant"] = (_metadata.ConstantTable.Length, trimmedCount, trimmedBytes);
                Console.WriteLine($"Constant: {trimmedCount}/{_metadata.ConstantTable.Length} rows trimmed ({trimmedBytes} bytes)");
            }

            // 剪裁 CustomAttribute 表
            if (_metadata.CustomAttributeTable != null)
            {
                int trimmedCount = 0;
                long trimmedBytes = 0;
                
                for (int i = 0; i < _metadata.CustomAttributeTable.Length; i++)
                {
                    var attr = _metadata.CustomAttributeTable[i];
                    uint parentToken = DecodeHasCustomAttribute(attr.Parent);
                    
                    if (!usedTokens.Contains(parentToken))
                    {
                        uint rowOffset = GetTableRowOffset(0x0C, i);
                        if (rowOffset > 0)
                        {
                            int rowSize = GetCodedIndexSize(new[] { 0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x14, 0x17, 0x20, 0x23, 0x26, 0x27, 0x28 }) +
                                         GetCodedIndexSize(new[] { 0x06, 0x0A }) + _metadata.BlobIndexSize;
                            ZeroBytes(rowOffset, (uint)rowSize);
                            trimmedCount++;
                            trimmedBytes += rowSize;
                        }
                    }
                }
                
                _deepTableStats["CustomAttribute"] = (_metadata.CustomAttributeTable.Length, trimmedCount, trimmedBytes);
                Console.WriteLine($"CustomAttribute: {trimmedCount}/{_metadata.CustomAttributeTable.Length} rows trimmed ({trimmedBytes} bytes)");
            }

            // 剪裁 StandAloneSig 表
            TrimTable("StandAloneSig", 0x11, _metadata.StandAloneSigTable?.Length ?? 0, usedTokens,
                i => _metadata.BlobIndexSize);

            // 剪裁 TypeSpec 表
            TrimTable("TypeSpec", 0x1B, _metadata.TypeSpecTable?.Length ?? 0, usedTokens,
                i => _metadata.BlobIndexSize);

            // 剪裁 MethodSpec 表
            TrimTable("MethodSpec", 0x2B, _metadata.MethodSpecTable?.Length ?? 0, usedTokens,
                i => GetCodedIndexSize(new[] { 0x06, 0x0A }) + _metadata.BlobIndexSize);

            // 剪裁 InterfaceImpl 表
            if (_metadata.InterfaceImplTable != null)
            {
                int trimmedCount = 0;
                long trimmedBytes = 0;
                
                for (int i = 0; i < _metadata.InterfaceImplTable.Length; i++)
                {
                    var impl = _metadata.InterfaceImplTable[i];
                    uint classToken = (uint)(0x02000000 | impl.Class);
                    
                    if (!usedTokens.Contains(classToken))
                    {
                        uint rowOffset = GetTableRowOffset(0x09, i);
                        if (rowOffset > 0)
                        {
                            int rowSize = GetTableIndexSize(0x02) + GetCodedIndexSize(new[] { 0x02, 0x01, 0x1B });
                            ZeroBytes(rowOffset, (uint)rowSize);
                            trimmedCount++;
                            trimmedBytes += rowSize;
                        }
                    }
                }
                
                _deepTableStats["InterfaceImpl"] = (_metadata.InterfaceImplTable.Length, trimmedCount, trimmedBytes);
                Console.WriteLine($"InterfaceImpl: {trimmedCount}/{_metadata.InterfaceImplTable.Length} rows trimmed ({trimmedBytes} bytes)");
            }

            // 计算总剪裁字节数
            foreach (var stat in _deepTableStats.Values)
            {
                _deepTrimmedBytes += stat.trimmedBytes;
            }
        }

        /// <summary>
        /// 剪裁单个表的辅助方法
        /// </summary>
        private void TrimTable(string tableName, int tableId, int rowCount, HashSet<uint> usedTokens, Func<int, int> getRowSize)
        {
            if (rowCount == 0) return;

            int trimmedCount = 0;
            long trimmedBytes = 0;

            for (int i = 0; i < rowCount; i++)
            {
                uint token = (uint)((tableId << 24) | (i + 1));
                
                if (!usedTokens.Contains(token))
                {
                    uint rowOffset = GetTableRowOffset(tableId, i);
                    if (rowOffset > 0)
                    {
                        int rowSize = getRowSize(i);
                        ZeroBytes(rowOffset, (uint)rowSize);
                        trimmedCount++;
                        trimmedBytes += rowSize;
                    }
                }
            }

            _deepTableStats[tableName] = (rowCount, trimmedCount, trimmedBytes);
            Console.WriteLine($"{tableName}: {trimmedCount}/{rowCount} rows trimmed ({trimmedBytes} bytes)");
        }

        /// <summary>
        /// 剪裁未使用的 Blob 堆数据
        /// </summary>
        private void TrimUnusedBlobData(HashSet<uint> usedTokens)
        {
            Console.WriteLine("\n=== Trimming Unused Blob Heap Data ===");

            if (!_metadata.Streams.ContainsKey("#Blob"))
            {
                Console.WriteLine("No #Blob heap found");
                return;
            }

            var blobStream = _metadata.Streams["#Blob"];
            long originalSize = blobStream.Data.Length;
            _deepTrimmedBlobBytes = 0;

            // 收集使用中的 Blob 偏移
            HashSet<uint> usedBlobOffsets = new HashSet<uint>();
            usedBlobOffsets.Add(0); // 总是保留偏移 0

            foreach (var token in usedTokens)
            {
                if ((token & 0x70000000) == 0x70000000)
                {
                    // 这是一个 Blob 偏移标记
                    uint blobOffset = token & 0x0FFFFFFF;
                    usedBlobOffsets.Add(blobOffset);
                }
            }

            Console.WriteLine($"Found {usedBlobOffsets.Count} used blob offsets");

            // 遍历 Blob 堆并清零未使用的数据
            uint offset = 1; // 跳过初始的 0 字节
            int trimmedCount = 0;
            
            while (offset < blobStream.Data.Length)
            {
                uint blobStart = offset;
                
                // 读取压缩长度
                int length;
                int headerSize;
                
                if ((blobStream.Data[offset] & 0x80) == 0)
                {
                    length = blobStream.Data[offset];
                    headerSize = 1;
                }
                else if ((blobStream.Data[offset] & 0xC0) == 0x80)
                {
                    if (offset + 1 >= blobStream.Data.Length) break;
                    length = ((blobStream.Data[offset] & 0x3F) << 8) | blobStream.Data[offset + 1];
                    headerSize = 2;
                }
                else if ((blobStream.Data[offset] & 0xE0) == 0xC0)
                {
                    if (offset + 3 >= blobStream.Data.Length) break;
                    length = ((blobStream.Data[offset] & 0x1F) << 24) | (blobStream.Data[offset + 1] << 16) |
                             (blobStream.Data[offset + 2] << 8) | blobStream.Data[offset + 3];
                    headerSize = 4;
                }
                else
                {
                    offset++;
                    continue;
                }

                // 检查是否使用
                if (!usedBlobOffsets.Contains(blobStart))
                {
                    // 清零数据内容（保留长度前缀）
                    uint fileOffset = blobStream.Offset + blobStart + (uint)headerSize;
                    ZeroBytes(fileOffset, (uint)length);
                    _deepTrimmedBlobBytes += length;
                    trimmedCount++;
                }

                offset = blobStart + (uint)headerSize + (uint)length;
            }

            Console.WriteLine($"Trimmed {trimmedCount} blob entries ({_deepTrimmedBlobBytes} bytes)");
            _deepTrimmedBytes += _deepTrimmedBlobBytes;
        }

        /// <summary>
        /// 剪裁未使用的 #US 堆数据
        /// </summary>
        private void TrimUnusedUserStrings(HashSet<uint> usedTokens)
        {
            Console.WriteLine("\n=== Trimming Unused #US Heap Data ===");

            if (!_metadata.Streams.ContainsKey("#US"))
            {
                Console.WriteLine("No #US heap found");
                return;
            }

            var usStream = _metadata.Streams["#US"];
            long originalSize = usStream.Data.Length;
            _deepTrimmedUSBytes = 0;

            // 收集使用中的 #US 偏移（从保留的方法体中提取 ldstr 指令）
            HashSet<uint> usedUSOffsets = new HashSet<uint>();
            usedUSOffsets.Add(0); // 总是保留偏移 0

            // 扫描所有保留的方法体
            if (_metadata.MethodDefTable != null && _metadata.TypeDefTable != null)
            {
                for (int typeIndex = 0; typeIndex < _metadata.TypeDefTable.Length; typeIndex++)
                {
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

                        if (ShouldTrimMethod(methodFullName))
                            continue;

                        // 提取 ldstr 指令的 #US 引用
                        if (method.RVA != 0)
                        {
                            ExtractUserStringReferences(method.RVA, usedUSOffsets);
                        }
                    }
                }
            }

            Console.WriteLine($"Found {usedUSOffsets.Count} used user string offsets");

            // 遍历 #US 堆并清零未使用的字符串
            uint offset = 1; // 跳过初始的 0 字节
            int trimmedCount = 0;
            
            while (offset < usStream.Data.Length)
            {
                uint stringStart = offset;
                
                // 读取压缩长度
                int length;
                int headerSize;
                
                if ((usStream.Data[offset] & 0x80) == 0)
                {
                    length = usStream.Data[offset];
                    headerSize = 1;
                }
                else if ((usStream.Data[offset] & 0xC0) == 0x80)
                {
                    if (offset + 1 >= usStream.Data.Length) break;
                    length = ((usStream.Data[offset] & 0x3F) << 8) | usStream.Data[offset + 1];
                    headerSize = 2;
                }
                else if ((usStream.Data[offset] & 0xE0) == 0xC0)
                {
                    if (offset + 3 >= usStream.Data.Length) break;
                    length = ((usStream.Data[offset] & 0x1F) << 24) | (usStream.Data[offset + 1] << 16) |
                             (usStream.Data[offset + 2] << 8) | usStream.Data[offset + 3];
                    headerSize = 4;
                }
                else
                {
                    offset++;
                    continue;
                }

                // 检查是否使用
                if (!usedUSOffsets.Contains(stringStart))
                {
                    // 清零字符串内容（保留长度前缀）
                    uint fileOffset = usStream.Offset + stringStart + (uint)headerSize;
                    ZeroBytes(fileOffset, (uint)length);
                    _deepTrimmedUSBytes += length;
                    trimmedCount++;
                }

                offset = stringStart + (uint)headerSize + (uint)length;
            }

            Console.WriteLine($"Trimmed {trimmedCount} user strings ({_deepTrimmedUSBytes} bytes)");
            _deepTrimmedBytes += _deepTrimmedUSBytes;
        }

        /// <summary>
        /// 从方法体中提取用户字符串引用
        /// </summary>
        private void ExtractUserStringReferences(uint rva, HashSet<uint> usedUSOffsets)
        {
            try
            {
                uint offset = RVAToFileOffset(rva);
                if (offset == 0 || offset >= _fileData.Length)
                    return;

                byte firstByte = _fileData[offset];
                byte[] ilCode;

                // 检查方法体格式
                if ((firstByte & 0x03) == 0x02) // Tiny format
                {
                    uint codeSize = (uint)(firstByte >> 2);
                    ilCode = new byte[codeSize];
                    Array.Copy(_fileData, offset + 1, ilCode, 0, codeSize);
                }
                else if ((firstByte & 0x03) == 0x03) // Fat format
                {
                    uint codeSize = BitConverter.ToUInt32(_fileData, (int)offset + 4);
                    ilCode = new byte[codeSize];
                    Array.Copy(_fileData, offset + 12, ilCode, 0, codeSize);
                }
                else
                {
                    return;
                }

                // 扫描 ldstr 指令 (0x72)
                int pos = 0;
                while (pos < ilCode.Length)
                {
                    if (ilCode[pos] == 0x72 && pos + 4 < ilCode.Length)
                    {
                        uint token = BitConverter.ToUInt32(ilCode, pos + 1);
                        // ldstr 的 token 格式：0x70xxxxxx，其中 xxxxxx 是 #US 偏移
                        if ((token & 0xFF000000) == 0x70000000)
                        {
                            uint usOffset = token & 0x00FFFFFF;
                            usedUSOffsets.Add(usOffset);
                        }
                        pos += 5;
                    }
                    else
                    {
                        pos++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to extract user string references from RVA 0x{rva:X}: {ex.Message}");
            }
        }

        /// <summary>
        /// 输出 S2 剪裁统计信息
        /// </summary>
        private void PrintDeepStatistics()
        {
            Console.WriteLine("\n=== Deep Trimming Statistics ===");
            
            Console.WriteLine("\nMetadata Tables:");
            foreach (var kvp in _deepTableStats.OrderBy(x => x.Key))
            {
                var (total, trimmed, bytes) = kvp.Value;
                int remaining = total - trimmed;
                double trimmedPercent = total > 0 ? (trimmed * 100.0 / total) : 0;
                double bytesPercent = _fileData.Length > 0 ? (bytes * 100.0 / _fileData.Length) : 0;
                
                Console.WriteLine($"  {kvp.Key}:");
                Console.WriteLine($"    Total rows: {total}, Trimmed: {trimmed} ({trimmedPercent:F2}%), Remaining: {remaining}");
                Console.WriteLine($"    Trimmed bytes: {bytes:N0} ({bytesPercent:F4}%)");
            }

            Console.WriteLine($"\n#Blob Heap:");
            if (_metadata.Streams.ContainsKey("#Blob"))
            {
                long blobSize = _metadata.Streams["#Blob"].Data.Length;
                long blobRemaining = blobSize - _deepTrimmedBlobBytes;
                Console.WriteLine($"  Total size: {blobSize:N0} bytes");
                Console.WriteLine($"  Trimmed: {_deepTrimmedBlobBytes:N0} bytes ({(_deepTrimmedBlobBytes * 100.0 / blobSize):F2}%)");
                Console.WriteLine($"  Remaining: {blobRemaining:N0} bytes ({(blobRemaining * 100.0 / blobSize):F2}%)");
            }

            Console.WriteLine($"\n#US Heap:");
            if (_metadata.Streams.ContainsKey("#US"))
            {
                long usSize = _metadata.Streams["#US"].Data.Length;
                long usRemaining = usSize - _deepTrimmedUSBytes;
                Console.WriteLine($"  Total size: {usSize:N0} bytes");
                Console.WriteLine($"  Trimmed: {_deepTrimmedUSBytes:N0} bytes ({(_deepTrimmedUSBytes * 100.0 / usSize):F2}%)");
                Console.WriteLine($"  Remaining: {usRemaining:N0} bytes ({(usRemaining * 100.0 / usSize):F2}%)");
            }

            Console.WriteLine($"\nDeep Trimmed: {_deepTrimmedBytes:N0} bytes ({(_deepTrimmedBytes * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Total Trimmed: {(_totalBytesZeroed + _deepTrimmedBytes):N0} bytes ({((_totalBytesZeroed + _deepTrimmedBytes) * 100.0 / _fileData.Length):F2}%)");
        }

        // 辅助方法：解码 Coded Index
        private uint DecodeHasConstant(uint codedIndex)
        {
            int tag = (int)(codedIndex & 0x03);
            int index = (int)(codedIndex >> 2);
            
            switch (tag)
            {
                case 0: return (uint)(0x04000000 | index); // Field
                case 1: return (uint)(0x08000000 | index); // Param
                case 2: return (uint)(0x17000000 | index); // Property
                default: return 0;
            }
        }

        private uint DecodeHasCustomAttribute(uint codedIndex)
        {
            int tag = (int)(codedIndex & 0x1F);
            int index = (int)(codedIndex >> 5);
            
            switch (tag)
            {
                case 0: return (uint)(0x06000000 | index); // MethodDef
                case 1: return (uint)(0x04000000 | index); // Field
                case 2: return (uint)(0x01000000 | index); // TypeRef
                case 3: return (uint)(0x02000000 | index); // TypeDef
                case 4: return (uint)(0x08000000 | index); // Param
                case 6: return (uint)(0x0A000000 | index); // MemberRef
                case 9: return (uint)(0x17000000 | index); // Property
                case 10: return (uint)(0x14000000 | index); // Event
                case 13: return (uint)(0x20000000 | index); // Assembly
                default: return 0;
            }
        }

        #endregion
    }
}
