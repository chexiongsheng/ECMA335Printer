using System.Text;

namespace ECMA335Printer
{
    /// <summary>
    /// PE文件剪裁器 - 类粒度剪裁
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
            int totalClassCount = _metadata.TypeDefTable.Length;
            _totalBytesZeroed = 0; // Reset byte counter

            // Iterate through all types
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
                    TrimType(typeIndex, typeDef);
                    trimmedClassCount++;
                }
            }

            Console.WriteLine($"\n=== Trimming Complete ===");
            Console.WriteLine($"Total types: {totalClassCount}");
            Console.WriteLine($"Trimmed types: {trimmedClassCount}");
            Console.WriteLine($"Remaining types: {totalClassCount - trimmedClassCount}");
            Console.WriteLine($"Total bytes: {_fileData.Length:N0}");
            Console.WriteLine($"Trimmed bytes: {_totalBytesZeroed:N0} ({(_totalBytesZeroed * 100.0 / _fileData.Length):F2}%)");
            Console.WriteLine($"Remaining bytes: {(_fileData.Length - _totalBytesZeroed):N0} ({((_fileData.Length - _totalBytesZeroed) * 100.0 / _fileData.Length):F2}%)");
            if (trimmedClassCount > 0)
            {
                Console.WriteLine($"Average bytes per trimmed type: {_totalBytesZeroed / trimmedClassCount:N0}");
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
        /// 剪裁指定类型（清零所有相关数据）
        /// </summary>
        private void TrimType(int typeIndex, TypeDefRow typeDef)
        {
            // 1. Trim all methods of this type
            TrimTypeMethods(typeIndex, typeDef);

            // 2. Trim all fields of this type
            TrimTypeFields(typeIndex, typeDef);

            // 3. Trim properties
            TrimTypeProperties(typeIndex);

            // 4. Trim events
            TrimTypeEvents(typeIndex);

            // 5. Zero out TypeDef row data (but keep the row structure)
            ZeroTypeDefRow(typeIndex);
        }

        /// <summary>
        /// 剪裁类型的所有方法
        /// </summary>
        private void TrimTypeMethods(int typeIndex, TypeDefRow typeDef)
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

                TrimMethod((int)methodIdx - 1); // Convert to 0-based index
            }
        }

        /// <summary>
        /// 剪裁单个方法
        /// </summary>
        private void TrimMethod(int methodIndex)
        {
            if (_metadata.MethodDefTable == null || methodIndex >= _metadata.MethodDefTable.Length)
                return;

            var method = _metadata.MethodDefTable[methodIndex];

            // 1. Zero method body (IL code)
            if (method.RVA != 0)
            {
                ZeroMethodBody(method.RVA);
            }

            // 2. Zero method signature in Blob heap
            if (method.Signature != 0)
            {
                ZeroBlobData(method.Signature);
            }

            // 3. Zero parameters
            TrimMethodParameters(methodIndex, method);

            // 4. Zero MethodDef row in metadata table
            ZeroMethodDefRow(methodIndex);
        }

        /// <summary>
        /// 剪裁方法参数
        /// </summary>
        private void TrimMethodParameters(int methodIndex, MethodDefRow method)
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

                ZeroParamRow((int)paramIdx - 1);
            }
        }

        /// <summary>
        /// 剪裁类型的所有字段
        /// </summary>
        private void TrimTypeFields(int typeIndex, TypeDefRow typeDef)
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

                TrimField((int)fieldIdx - 1);
            }
        }

        /// <summary>
        /// 剪裁单个字段
        /// </summary>
        private void TrimField(int fieldIndex)
        {
            if (_metadata.FieldTable == null || fieldIndex >= _metadata.FieldTable.Length)
                return;

            var field = _metadata.FieldTable[fieldIndex];

            // 1. Zero field signature in Blob heap
            if (field.Signature != 0)
            {
                ZeroBlobData(field.Signature);
            }

            // 2. Zero FieldRVA if exists
            if (_metadata.FieldRVATable != null)
            {
                foreach (var fieldRVA in _metadata.FieldRVATable)
                {
                    if (fieldRVA.Field == fieldIndex + 1) // 1-based
                    {
                        ZeroFieldRVAData(fieldRVA.RVA);
                    }
                }
            }

            // 3. Zero Field row in metadata table
            ZeroFieldRow(fieldIndex);
        }

        /// <summary>
        /// 剪裁类型的属性
        /// </summary>
        private void TrimTypeProperties(int typeIndex)
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
        /// 剪裁类型的事件
        /// </summary>
        private void TrimTypeEvents(int typeIndex)
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

        #region Zero Operations

        /// <summary>
        /// 清零方法体
        /// </summary>
        private void ZeroMethodBody(uint rva)
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
                    ZeroBytes(offset, totalSize);
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
                    
                    ZeroBytes(offset, totalSize);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to zero method body at RVA 0x{rva:X}: {ex.Message}");
            }
        }

        /// <summary>
        /// 清零Blob数据
        /// </summary>
        private void ZeroBlobData(uint blobOffset)
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

            // Zero the blob data (but keep the length header)
            ZeroBytes((uint)(pos + headerSize), (uint)length);
        }

        /// <summary>
        /// 清零FieldRVA数据
        /// </summary>
        private void ZeroFieldRVAData(uint rva)
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
        /// 清零TypeDef行
        /// </summary>
        private void ZeroTypeDefRow(int typeIndex)
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
                // Zero only the first 4 fields, keep FieldList and MethodList for index integrity
                int bytesToZero = 4 + _metadata.StringIndexSize * 2 + GetCodedIndexSize(new[] { 0x02, 0x01, 0x1B });
                ZeroBytes(rowOffset, (uint)bytesToZero);
            }
        }

        /// <summary>
        /// 清零MethodDef行
        /// </summary>
        private void ZeroMethodDefRow(int methodIndex)
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
                // Zero all fields except ParamList (keep it for index integrity)
                int bytesToZero = 4 + 2 + 2 + _metadata.StringIndexSize + _metadata.BlobIndexSize;
                ZeroBytes(rowOffset, (uint)bytesToZero);
            }
        }

        /// <summary>
        /// 清零Field行
        /// </summary>
        private void ZeroFieldRow(int fieldIndex)
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
                ZeroBytes(rowOffset, (uint)rowSize);
            }
        }

        /// <summary>
        /// 清零Param行
        /// </summary>
        private void ZeroParamRow(int paramIndex)
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
                ZeroBytes(rowOffset, (uint)rowSize);
            }
        }

        /// <summary>
        /// 清零指定范围的字节
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
