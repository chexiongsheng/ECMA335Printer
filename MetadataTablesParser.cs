using System.Text;

namespace ECMA335Printer
{
    /// <summary>
    /// 元数据表解析器
    /// </summary>
    class MetadataTablesParser
    {
        private readonly BinaryReader _reader;
        private readonly MetadataRoot _metadata;
        private readonly byte[] _fileData;
        private readonly List<Section> _sections;

        public MetadataTablesParser(BinaryReader reader, MetadataRoot metadata, byte[] fileData, List<Section> sections)
        {
            _reader = reader;
            _metadata = metadata;
            _fileData = fileData;
            _sections = sections;
        }

        public void ParseTables()
        {
            _reader.ReadUInt32(); // Reserved
            byte majorVersion = _reader.ReadByte();
            byte minorVersion = _reader.ReadByte();
            byte heapSizes = _reader.ReadByte();
            _reader.ReadByte(); // Reserved

            ulong valid = _reader.ReadUInt64();
            ulong sorted = _reader.ReadUInt64();

            // Determine heap index sizes
            bool stringHeapBig = (heapSizes & 0x01) != 0;
            bool guidHeapBig = (heapSizes & 0x02) != 0;
            bool blobHeapBig = (heapSizes & 0x04) != 0;

            _metadata.StringIndexSize = stringHeapBig ? 4 : 2;
            _metadata.GuidIndexSize = guidHeapBig ? 4 : 2;
            _metadata.BlobIndexSize = blobHeapBig ? 4 : 2;

            // Read row counts for each table
            int[] rowCounts = new int[64];
            for (int i = 0; i < 64; i++)
            {
                if ((valid & (1UL << i)) != 0)
                {
                    rowCounts[i] = _reader.ReadInt32();
                }
            }

            _metadata.TableRowCounts = rowCounts;

            // Parse all tables
            ParseAllTables();
        }

        private void ParseAllTables()
        {
            var counts = _metadata.TableRowCounts;

            // Parse each table in order
            if (counts[0x00] > 0) _metadata.ModuleTable = ParseModuleTable(counts[0x00]);
            if (counts[0x01] > 0) _metadata.TypeRefTable = ParseTypeRefTable(counts[0x01]);
            if (counts[0x02] > 0) _metadata.TypeDefTable = ParseTypeDefTable(counts[0x02]);
            if (counts[0x03] > 0) _metadata.FieldPtrTable = ParseFieldPtrTable(counts[0x03]);
            if (counts[0x04] > 0) _metadata.FieldTable = ParseFieldTable(counts[0x04]);
            if (counts[0x05] > 0) _metadata.MethodPtrTable = ParseMethodPtrTable(counts[0x05]);
            if (counts[0x06] > 0) _metadata.MethodDefTable = ParseMethodDefTable(counts[0x06]);
            if (counts[0x07] > 0) _metadata.ParamPtrTable = ParseParamPtrTable(counts[0x07]);
            if (counts[0x08] > 0) _metadata.ParamTable = ParseParamTable(counts[0x08]);
            if (counts[0x09] > 0) _metadata.InterfaceImplTable = ParseInterfaceImplTable(counts[0x09]);
            if (counts[0x0A] > 0) _metadata.MemberRefTable = ParseMemberRefTable(counts[0x0A]);
            if (counts[0x0B] > 0) _metadata.ConstantTable = ParseConstantTable(counts[0x0B]);
            if (counts[0x0C] > 0) _metadata.CustomAttributeTable = ParseCustomAttributeTable(counts[0x0C]);
            if (counts[0x0D] > 0) _metadata.FieldMarshalTable = ParseFieldMarshalTable(counts[0x0D]);
            if (counts[0x0E] > 0) _metadata.DeclSecurityTable = ParseDeclSecurityTable(counts[0x0E]);
            if (counts[0x0F] > 0) _metadata.ClassLayoutTable = ParseClassLayoutTable(counts[0x0F]);
            if (counts[0x10] > 0) _metadata.FieldLayoutTable = ParseFieldLayoutTable(counts[0x10]);
            if (counts[0x11] > 0) _metadata.StandAloneSigTable = ParseStandAloneSigTable(counts[0x11]);
            if (counts[0x12] > 0) _metadata.EventMapTable = ParseEventMapTable(counts[0x12]);
            if (counts[0x13] > 0) _metadata.EventPtrTable = ParseEventPtrTable(counts[0x13]);
            if (counts[0x14] > 0) _metadata.EventTable = ParseEventTable(counts[0x14]);
            if (counts[0x15] > 0) _metadata.PropertyMapTable = ParsePropertyMapTable(counts[0x15]);
            if (counts[0x16] > 0) _metadata.PropertyPtrTable = ParsePropertyPtrTable(counts[0x16]);
            if (counts[0x17] > 0) _metadata.PropertyTable = ParsePropertyTable(counts[0x17]);
            if (counts[0x18] > 0) _metadata.MethodSemanticsTable = ParseMethodSemanticsTable(counts[0x18]);
            if (counts[0x19] > 0) _metadata.MethodImplTable = ParseMethodImplTable(counts[0x19]);
            if (counts[0x1A] > 0) _metadata.ModuleRefTable = ParseModuleRefTable(counts[0x1A]);
            if (counts[0x1B] > 0) _metadata.TypeSpecTable = ParseTypeSpecTable(counts[0x1B]);
            if (counts[0x1C] > 0) _metadata.ImplMapTable = ParseImplMapTable(counts[0x1C]);
            if (counts[0x1D] > 0) _metadata.FieldRVATable = ParseFieldRVATable(counts[0x1D]);
            if (counts[0x1E] > 0) _metadata.ENCLogTable = ParseENCLogTable(counts[0x1E]);
            if (counts[0x1F] > 0) _metadata.ENCMapTable = ParseENCMapTable(counts[0x1F]);
            if (counts[0x20] > 0) _metadata.AssemblyTable = ParseAssemblyTable(counts[0x20]);
            if (counts[0x21] > 0) _metadata.AssemblyProcessorTable = ParseAssemblyProcessorTable(counts[0x21]);
            if (counts[0x22] > 0) _metadata.AssemblyOSTable = ParseAssemblyOSTable(counts[0x22]);
            if (counts[0x23] > 0) _metadata.AssemblyRefTable = ParseAssemblyRefTable(counts[0x23]);
            if (counts[0x24] > 0) _metadata.AssemblyRefProcessorTable = ParseAssemblyRefProcessorTable(counts[0x24]);
            if (counts[0x25] > 0) _metadata.AssemblyRefOSTable = ParseAssemblyRefOSTable(counts[0x25]);
            if (counts[0x26] > 0) _metadata.FileTable = ParseFileTable(counts[0x26]);
            if (counts[0x27] > 0) _metadata.ExportedTypeTable = ParseExportedTypeTable(counts[0x27]);
            if (counts[0x28] > 0) _metadata.ManifestResourceTable = ParseManifestResourceTable(counts[0x28]);
            if (counts[0x29] > 0) _metadata.NestedClassTable = ParseNestedClassTable(counts[0x29]);
            if (counts[0x2A] > 0) _metadata.GenericParamTable = ParseGenericParamTable(counts[0x2A]);
            if (counts[0x2B] > 0) _metadata.MethodSpecTable = ParseMethodSpecTable(counts[0x2B]);
            if (counts[0x2C] > 0) _metadata.GenericParamConstraintTable = ParseGenericParamConstraintTable(counts[0x2C]);
        }

        #region Helper Methods

        private uint ReadIndex(int indexSize)
        {
            return indexSize == 2 ? _reader.ReadUInt16() : _reader.ReadUInt32();
        }

        private uint ReadStringIndex() => ReadIndex(_metadata.StringIndexSize);
        private uint ReadGuidIndex() => ReadIndex(_metadata.GuidIndexSize);
        private uint ReadBlobIndex() => ReadIndex(_metadata.BlobIndexSize);

        private uint ReadTableIndex(int tableId)
        {
            int rowCount = _metadata.TableRowCounts[tableId];
            return rowCount < 65536 ? _reader.ReadUInt16() : _reader.ReadUInt32();
        }

        private uint ReadCodedIndex(int[] tables)
        {
            int tagBits = tables.Length <= 2 ? 1 : tables.Length <= 4 ? 2 : tables.Length <= 8 ? 3 : tables.Length <= 16 ? 4 : 5;
            int maxRows = tables.Max(t => _metadata.TableRowCounts[t]);
            bool isSmall = maxRows < (65536 >> tagBits);
            return isSmall ? _reader.ReadUInt16() : _reader.ReadUInt32();
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

        public Guid ReadGuid(uint index)
        {
            if (!_metadata.Streams.ContainsKey("#GUID") || index == 0)
                return Guid.Empty;

            var data = _metadata.Streams["#GUID"].Data;
            int offset = ((int)index - 1) * 16; // GUID index is 1-based, each GUID is 16 bytes

            if (offset < 0 || offset + 16 > data.Length)
                return Guid.Empty;

            byte[] guidBytes = new byte[16];
            Array.Copy(data, offset, guidBytes, 0, 16);
            return new Guid(guidBytes);
        }

        public byte[] ReadBlob(uint offset)
        {
            if (!_metadata.Streams.ContainsKey("#Blob") || offset == 0)
                return Array.Empty<byte>();

            var data = _metadata.Streams["#Blob"].Data;
            if (offset >= data.Length)
                return Array.Empty<byte>();

            // Read the compressed length
            int pos = (int)offset;
            int length;

            if ((data[pos] & 0x80) == 0)
            {
                // 1-byte length
                length = data[pos];
                pos++;
            }
            else if ((data[pos] & 0xC0) == 0x80)
            {
                // 2-byte length
                length = ((data[pos] & 0x3F) << 8) | data[pos + 1];
                pos += 2;
            }
            else if ((data[pos] & 0xE0) == 0xC0)
            {
                // 4-byte length
                length = ((data[pos] & 0x1F) << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
                pos += 4;
            }
            else
            {
                return Array.Empty<byte>();
            }

            if (pos + length > data.Length)
                return Array.Empty<byte>();

            byte[] result = new byte[length];
            Array.Copy(data, pos, result, 0, length);
            return result;
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

        private MethodBody? ParseMethodBody(uint rva)
        {
            if (rva == 0)
                return null;

            uint offset = RVAToFileOffset(rva);
            if (offset == 0 || offset >= _fileData.Length)
                return null;

            var body = new MethodBody();
            byte firstByte = _fileData[offset];

            // Check if tiny or fat format
            if ((firstByte & 0x03) == 0x02) // Tiny format
            {
                body.IsTiny = true;
                body.CodeSize = (uint)(firstByte >> 2);
                body.MaxStack = 8;
                body.LocalVarSigTok = 0;

                if (offset + 1 + body.CodeSize <= _fileData.Length)
                {
                    body.ILCode = new byte[body.CodeSize];
                    Array.Copy(_fileData, offset + 1, body.ILCode, 0, body.CodeSize);
                }
            }
            else if ((firstByte & 0x03) == 0x03) // Fat format
            {
                body.IsTiny = false;
                ushort flags = (ushort)(firstByte | (_fileData[offset + 1] << 8));
                body.MaxStack = (ushort)(_fileData[offset + 2] | (_fileData[offset + 3] << 8));
                body.CodeSize = BitConverter.ToUInt32(_fileData, (int)offset + 4);
                body.LocalVarSigTok = BitConverter.ToUInt32(_fileData, (int)offset + 8);

                uint codeOffset = offset + 12;
                if (codeOffset + body.CodeSize <= _fileData.Length)
                {
                    body.ILCode = new byte[body.CodeSize];
                    Array.Copy(_fileData, codeOffset, body.ILCode, 0, body.CodeSize);
                }

                // Parse exception handling clauses if present
                if ((flags & 0x08) != 0) // MoreSects flag
                {
                    uint sectOffset = codeOffset + body.CodeSize;
                    // Align to 4 bytes
                    sectOffset = (sectOffset + 3) & ~3u;

                    if (sectOffset < _fileData.Length)
                    {
                        byte sectFlags = _fileData[sectOffset];
                        if ((sectFlags & 0x01) != 0) // EHTable
                        {
                            bool fatFormat = (sectFlags & 0x40) != 0;
                            uint dataSize = fatFormat ?
                                BitConverter.ToUInt32(_fileData, (int)sectOffset) >> 8 :
                                _fileData[sectOffset + 1];

                            int clauseSize = fatFormat ? 24 : 12;
                            int clauseCount = (int)((dataSize - 4) / clauseSize);

                            body.ExceptionClauses = new ExceptionHandlingClause[clauseCount];
                            uint clauseOffset = sectOffset + 4;

                            for (int i = 0; i < clauseCount; i++)
                            {
                                var clause = new ExceptionHandlingClause();
                                if (fatFormat)
                                {
                                    clause.Flags = BitConverter.ToUInt32(_fileData, (int)clauseOffset);
                                    clause.TryOffset = BitConverter.ToUInt32(_fileData, (int)clauseOffset + 4);
                                    clause.TryLength = BitConverter.ToUInt32(_fileData, (int)clauseOffset + 8);
                                    clause.HandlerOffset = BitConverter.ToUInt32(_fileData, (int)clauseOffset + 12);
                                    clause.HandlerLength = BitConverter.ToUInt32(_fileData, (int)clauseOffset + 16);
                                    clause.ClassTokenOrFilterOffset = BitConverter.ToUInt32(_fileData, (int)clauseOffset + 20);
                                }
                                else
                                {
                                    clause.Flags = BitConverter.ToUInt16(_fileData, (int)clauseOffset);
                                    clause.TryOffset = BitConverter.ToUInt16(_fileData, (int)clauseOffset + 2);
                                    clause.TryLength = _fileData[clauseOffset + 4];
                                    clause.HandlerOffset = BitConverter.ToUInt16(_fileData, (int)clauseOffset + 5);
                                    clause.HandlerLength = _fileData[clauseOffset + 7];
                                    clause.ClassTokenOrFilterOffset = BitConverter.ToUInt32(_fileData, (int)clauseOffset + 8);
                                }
                                body.ExceptionClauses[i] = clause;
                                clauseOffset += (uint)clauseSize;
                            }
                        }
                    }
                }
            }

            return body;
        }

        #endregion

        #region Table Parsers

        private ModuleRow[] ParseModuleTable(int count)
        {
            var table = new ModuleRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ModuleRow
                {
                    Generation = _reader.ReadUInt16(),
                    Name = ReadStringIndex(),
                    Mvid = ReadGuidIndex(),
                    EncId = ReadGuidIndex(),
                    EncBaseId = ReadGuidIndex()
                };
            }
            return table;
        }

        private TypeRefRow[] ParseTypeRefTable(int count)
        {
            var table = new TypeRefRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new TypeRefRow
                {
                    ResolutionScope = ReadCodedIndex(new[] { 0x00, 0x1A, 0x23, 0x01 }), // Module, ModuleRef, AssemblyRef, TypeRef
                    TypeName = ReadStringIndex(),
                    TypeNamespace = ReadStringIndex()
                };
            }
            return table;
        }

        private TypeDefRow[] ParseTypeDefTable(int count)
        {
            var table = new TypeDefRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new TypeDefRow
                {
                    Flags = _reader.ReadUInt32(),
                    TypeName = ReadStringIndex(),
                    TypeNamespace = ReadStringIndex(),
                    Extends = ReadCodedIndex(new[] { 0x02, 0x01, 0x1B }), // TypeDef, TypeRef, TypeSpec
                    FieldList = ReadTableIndex(0x04),
                    MethodList = ReadTableIndex(0x06)
                };
            }
            return table;
        }

        private FieldPtrRow[] ParseFieldPtrTable(int count)
        {
            var table = new FieldPtrRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new FieldPtrRow { Field = ReadTableIndex(0x04) };
            }
            return table;
        }

        private FieldRow[] ParseFieldTable(int count)
        {
            var table = new FieldRow[count];
            for (int i = 0; i < count; i++)
            {
                // Read in correct order: Flags, Name, Signature
                ushort flags = _reader.ReadUInt16();
                uint nameIndex = ReadStringIndex();
                uint sigIndex = ReadBlobIndex();
                
                table[i] = new FieldRow
                {
                    Flags = flags,
                    Name = nameIndex,
                    Signature = sigIndex
                };

                // Resolve name and signature
                table[i].NameString = ReadString(nameIndex);
                
                byte[] sigData = ReadBlob(sigIndex);
                if (sigData.Length > 0)
                {
                    try
                    {
                        var parser = new SignatureParser(sigData);
                        table[i].ParsedSignature = parser.ParseFieldSignature();
                    }
                    catch
                    {
                        // If parsing fails, leave ParsedSignature as null
                    }
                }
            }
            return table;
        }

        private MethodPtrRow[] ParseMethodPtrTable(int count)
        {
            var table = new MethodPtrRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new MethodPtrRow { Method = ReadTableIndex(0x06) };
            }
            return table;
        }

        private MethodDefRow[] ParseMethodDefTable(int count)
        {
            var table = new MethodDefRow[count];
            for (int i = 0; i < count; i++)
            {
                uint rva = _reader.ReadUInt32();
                ushort implFlags = _reader.ReadUInt16();
                ushort flags = _reader.ReadUInt16();
                uint nameIndex = ReadStringIndex();
                uint sigIndex = ReadBlobIndex();
                uint paramList = ReadTableIndex(0x08);
                
                table[i] = new MethodDefRow
                {
                    RVA = rva,
                    ImplFlags = implFlags,
                    Flags = flags,
                    Name = nameIndex,
                    Signature = sigIndex,
                    ParamList = paramList
                };

                // Resolve name, signature, and method body
                table[i].NameString = ReadString(nameIndex);
                
                byte[] sigData = ReadBlob(sigIndex);
                if (sigData.Length > 0)
                {
                    try
                    {
                        var parser = new SignatureParser(sigData);
                        table[i].ParsedSignature = parser.ParseMethodSignature();
                    }
                    catch
                    {
                        // If parsing fails, leave ParsedSignature as null
                    }
                }
                
                table[i].MethodBody = ParseMethodBody(rva);
            }
            return table;
        }

        private ParamPtrRow[] ParseParamPtrTable(int count)
        {
            var table = new ParamPtrRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ParamPtrRow { Param = ReadTableIndex(0x08) };
            }
            return table;
        }

        private ParamRow[] ParseParamTable(int count)
        {
            var table = new ParamRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ParamRow
                {
                    Flags = _reader.ReadUInt16(),
                    Sequence = _reader.ReadUInt16(),
                    Name = ReadStringIndex()
                };
            }
            return table;
        }

        private InterfaceImplRow[] ParseInterfaceImplTable(int count)
        {
            var table = new InterfaceImplRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new InterfaceImplRow
                {
                    Class = ReadTableIndex(0x02),
                    Interface = ReadCodedIndex(new[] { 0x02, 0x01, 0x1B })
                };
            }
            return table;
        }

        private MemberRefRow[] ParseMemberRefTable(int count)
        {
            var table = new MemberRefRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new MemberRefRow
                {
                    Class = ReadCodedIndex(new[] { 0x02, 0x01, 0x1A, 0x06, 0x1B }),
                    Name = ReadStringIndex(),
                    Signature = ReadBlobIndex()
                };
            }
            return table;
        }

        private ConstantRow[] ParseConstantTable(int count)
        {
            var table = new ConstantRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ConstantRow
                {
                    Type = _reader.ReadByte(),
                    Padding = _reader.ReadByte(),
                    Parent = ReadCodedIndex(new[] { 0x04, 0x08, 0x17 }),
                    Value = ReadBlobIndex()
                };
            }
            return table;
        }

        private CustomAttributeRow[] ParseCustomAttributeTable(int count)
        {
            var table = new CustomAttributeRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new CustomAttributeRow
                {
                    Parent = ReadCodedIndex(new[] { 0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2C }),
                    Type = ReadCodedIndex(new[] { 0x06, 0x0A }),
                    Value = ReadBlobIndex()
                };
            }
            return table;
        }

        private FieldMarshalRow[] ParseFieldMarshalTable(int count)
        {
            var table = new FieldMarshalRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new FieldMarshalRow
                {
                    Parent = ReadCodedIndex(new[] { 0x04, 0x08 }),
                    NativeType = ReadBlobIndex()
                };
            }
            return table;
        }

        private DeclSecurityRow[] ParseDeclSecurityTable(int count)
        {
            var table = new DeclSecurityRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new DeclSecurityRow
                {
                    Action = _reader.ReadUInt16(),
                    Parent = ReadCodedIndex(new[] { 0x02, 0x06, 0x20 }),
                    PermissionSet = ReadBlobIndex()
                };
            }
            return table;
        }

        private ClassLayoutRow[] ParseClassLayoutTable(int count)
        {
            var table = new ClassLayoutRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ClassLayoutRow
                {
                    PackingSize = _reader.ReadUInt16(),
                    ClassSize = _reader.ReadUInt32(),
                    Parent = ReadTableIndex(0x02)
                };
            }
            return table;
        }

        private FieldLayoutRow[] ParseFieldLayoutTable(int count)
        {
            var table = new FieldLayoutRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new FieldLayoutRow
                {
                    Offset = _reader.ReadUInt32(),
                    Field = ReadTableIndex(0x04)
                };
            }
            return table;
        }

        private StandAloneSigRow[] ParseStandAloneSigTable(int count)
        {
            var table = new StandAloneSigRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new StandAloneSigRow
                {
                    Signature = ReadBlobIndex()
                };
            }
            return table;
        }

        private EventMapRow[] ParseEventMapTable(int count)
        {
            var table = new EventMapRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new EventMapRow
                {
                    Parent = ReadTableIndex(0x02),
                    EventList = ReadTableIndex(0x14)
                };
            }
            return table;
        }

        private EventPtrRow[] ParseEventPtrTable(int count)
        {
            var table = new EventPtrRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new EventPtrRow { Event = ReadTableIndex(0x14) };
            }
            return table;
        }

        private EventRow[] ParseEventTable(int count)
        {
            var table = new EventRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new EventRow
                {
                    EventFlags = _reader.ReadUInt16(),
                    Name = ReadStringIndex(),
                    EventType = ReadCodedIndex(new[] { 0x02, 0x01, 0x1B })
                };
            }
            return table;
        }

        private PropertyMapRow[] ParsePropertyMapTable(int count)
        {
            var table = new PropertyMapRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new PropertyMapRow
                {
                    Parent = ReadTableIndex(0x02),
                    PropertyList = ReadTableIndex(0x17)
                };
            }
            return table;
        }

        private PropertyPtrRow[] ParsePropertyPtrTable(int count)
        {
            var table = new PropertyPtrRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new PropertyPtrRow { Property = ReadTableIndex(0x17) };
            }
            return table;
        }

        private PropertyRow[] ParsePropertyTable(int count)
        {
            var table = new PropertyRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new PropertyRow
                {
                    Flags = _reader.ReadUInt16(),
                    Name = ReadStringIndex(),
                    Type = ReadBlobIndex()
                };
            }
            return table;
        }

        private MethodSemanticsRow[] ParseMethodSemanticsTable(int count)
        {
            var table = new MethodSemanticsRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new MethodSemanticsRow
                {
                    Semantics = _reader.ReadUInt16(),
                    Method = ReadTableIndex(0x06),
                    Association = ReadCodedIndex(new[] { 0x14, 0x17 })
                };
            }
            return table;
        }

        private MethodImplRow[] ParseMethodImplTable(int count)
        {
            var table = new MethodImplRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new MethodImplRow
                {
                    Class = ReadTableIndex(0x02),
                    MethodBody = ReadCodedIndex(new[] { 0x06, 0x0A }),
                    MethodDeclaration = ReadCodedIndex(new[] { 0x06, 0x0A })
                };
            }
            return table;
        }

        private ModuleRefRow[] ParseModuleRefTable(int count)
        {
            var table = new ModuleRefRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ModuleRefRow
                {
                    Name = ReadStringIndex()
                };
            }
            return table;
        }

        private TypeSpecRow[] ParseTypeSpecTable(int count)
        {
            var table = new TypeSpecRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new TypeSpecRow
                {
                    Signature = ReadBlobIndex()
                };
            }
            return table;
        }

        private ImplMapRow[] ParseImplMapTable(int count)
        {
            var table = new ImplMapRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ImplMapRow
                {
                    MappingFlags = _reader.ReadUInt16(),
                    MemberForwarded = ReadCodedIndex(new[] { 0x04, 0x06 }),
                    ImportName = ReadStringIndex(),
                    ImportScope = ReadTableIndex(0x1A)
                };
            }
            return table;
        }

        private FieldRVARow[] ParseFieldRVATable(int count)
        {
            var table = new FieldRVARow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new FieldRVARow
                {
                    RVA = _reader.ReadUInt32(),
                    Field = ReadTableIndex(0x04)
                };
            }
            return table;
        }

        private ENCLogRow[] ParseENCLogTable(int count)
        {
            var table = new ENCLogRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ENCLogRow
                {
                    Token = _reader.ReadUInt32(),
                    FuncCode = _reader.ReadUInt32()
                };
            }
            return table;
        }

        private ENCMapRow[] ParseENCMapTable(int count)
        {
            var table = new ENCMapRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ENCMapRow
                {
                    Token = _reader.ReadUInt32()
                };
            }
            return table;
        }

        private AssemblyRow[] ParseAssemblyTable(int count)
        {
            var table = new AssemblyRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new AssemblyRow
                {
                    HashAlgId = _reader.ReadUInt32(),
                    MajorVersion = _reader.ReadUInt16(),
                    MinorVersion = _reader.ReadUInt16(),
                    BuildNumber = _reader.ReadUInt16(),
                    RevisionNumber = _reader.ReadUInt16(),
                    Flags = _reader.ReadUInt32(),
                    PublicKey = ReadBlobIndex(),
                    Name = ReadStringIndex(),
                    Culture = ReadStringIndex()
                };
            }
            return table;
        }

        private AssemblyProcessorRow[] ParseAssemblyProcessorTable(int count)
        {
            var table = new AssemblyProcessorRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new AssemblyProcessorRow
                {
                    Processor = _reader.ReadUInt32()
                };
            }
            return table;
        }

        private AssemblyOSRow[] ParseAssemblyOSTable(int count)
        {
            var table = new AssemblyOSRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new AssemblyOSRow
                {
                    OSPlatformID = _reader.ReadUInt32(),
                    OSMajorVersion = _reader.ReadUInt32(),
                    OSMinorVersion = _reader.ReadUInt32()
                };
            }
            return table;
        }

        private AssemblyRefRow[] ParseAssemblyRefTable(int count)
        {
            var table = new AssemblyRefRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new AssemblyRefRow
                {
                    MajorVersion = _reader.ReadUInt16(),
                    MinorVersion = _reader.ReadUInt16(),
                    BuildNumber = _reader.ReadUInt16(),
                    RevisionNumber = _reader.ReadUInt16(),
                    Flags = _reader.ReadUInt32(),
                    PublicKeyOrToken = ReadBlobIndex(),
                    Name = ReadStringIndex(),
                    Culture = ReadStringIndex(),
                    HashValue = ReadBlobIndex()
                };
            }
            return table;
        }

        private AssemblyRefProcessorRow[] ParseAssemblyRefProcessorTable(int count)
        {
            var table = new AssemblyRefProcessorRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new AssemblyRefProcessorRow
                {
                    Processor = _reader.ReadUInt32(),
                    AssemblyRef = ReadTableIndex(0x23)
                };
            }
            return table;
        }

        private AssemblyRefOSRow[] ParseAssemblyRefOSTable(int count)
        {
            var table = new AssemblyRefOSRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new AssemblyRefOSRow
                {
                    OSPlatformID = _reader.ReadUInt32(),
                    OSMajorVersion = _reader.ReadUInt32(),
                    OSMinorVersion = _reader.ReadUInt32(),
                    AssemblyRef = ReadTableIndex(0x23)
                };
            }
            return table;
        }

        private FileRow[] ParseFileTable(int count)
        {
            var table = new FileRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new FileRow
                {
                    Flags = _reader.ReadUInt32(),
                    Name = ReadStringIndex(),
                    HashValue = ReadBlobIndex()
                };
            }
            return table;
        }

        private ExportedTypeRow[] ParseExportedTypeTable(int count)
        {
            var table = new ExportedTypeRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ExportedTypeRow
                {
                    Flags = _reader.ReadUInt32(),
                    TypeDefId = _reader.ReadUInt32(),
                    TypeName = ReadStringIndex(),
                    TypeNamespace = ReadStringIndex(),
                    Implementation = ReadCodedIndex(new[] { 0x26, 0x23, 0x27 })
                };
            }
            return table;
        }

        private ManifestResourceRow[] ParseManifestResourceTable(int count)
        {
            var table = new ManifestResourceRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new ManifestResourceRow
                {
                    Offset = _reader.ReadUInt32(),
                    Flags = _reader.ReadUInt32(),
                    Name = ReadStringIndex(),
                    Implementation = ReadCodedIndex(new[] { 0x26, 0x23 })
                };
            }
            return table;
        }

        private NestedClassRow[] ParseNestedClassTable(int count)
        {
            var table = new NestedClassRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new NestedClassRow
                {
                    NestedClass = ReadTableIndex(0x02),
                    EnclosingClass = ReadTableIndex(0x02)
                };
            }
            return table;
        }

        private GenericParamRow[] ParseGenericParamTable(int count)
        {
            var table = new GenericParamRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new GenericParamRow
                {
                    Number = _reader.ReadUInt16(),
                    Flags = _reader.ReadUInt16(),
                    Owner = ReadCodedIndex(new[] { 0x02, 0x06 }),
                    Name = ReadStringIndex()
                };
            }
            return table;
        }

        private MethodSpecRow[] ParseMethodSpecTable(int count)
        {
            var table = new MethodSpecRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new MethodSpecRow
                {
                    Method = ReadCodedIndex(new[] { 0x06, 0x0A }),
                    Instantiation = ReadBlobIndex()
                };
            }
            return table;
        }

        private GenericParamConstraintRow[] ParseGenericParamConstraintTable(int count)
        {
            var table = new GenericParamConstraintRow[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = new GenericParamConstraintRow
                {
                    Owner = ReadTableIndex(0x2A),
                    Constraint = ReadCodedIndex(new[] { 0x02, 0x01, 0x1B })
                };
            }
            return table;
        }

        #endregion

        public static string[] GetTableNames()
        {
            return new string[]
            {
                "Module", "TypeRef", "TypeDef", "Field_Ptr", "Field", "Method_Ptr",
                "MethodDef", "Param_Ptr", "Param", "InterfaceImpl", "MemberRef", "Constant",
                "CustomAttribute", "FieldMarshal", "DeclSecurity", "ClassLayout", "FieldLayout", "StandAloneSig",
                "EventMap", "Event_Ptr", "Event", "PropertyMap", "Property_Ptr", "Property",
                "MethodSemantics", "MethodImpl", "ModuleRef", "TypeSpec", "ImplMap", "FieldRVA",
                "ENCLog", "ENCMap", "Assembly", "AssemblyProcessor", "AssemblyOS", "AssemblyRef",
                "AssemblyRefProcessor", "AssemblyRefOS", "File", "ExportedType", "ManifestResource", "NestedClass",
                "GenericParam", "MethodSpec", "GenericParamConstraint"
            };
        }
    }
}
