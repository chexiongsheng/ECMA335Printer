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

        public MetadataTablesParser(BinaryReader reader, MetadataRoot metadata)
        {
            _reader = reader;
            _metadata = metadata;
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
        }

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
