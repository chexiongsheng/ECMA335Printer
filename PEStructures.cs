namespace ECMA335Printer
{
    /// <summary>
    /// PE文件的节（Section）信息
    /// </summary>
    class Section
    {
        public string Name { get; set; } = string.Empty;
        public uint VirtualSize { get; set; }
        public uint VirtualAddress { get; set; }
        public uint SizeOfRawData { get; set; }
        public uint PointerToRawData { get; set; }
        public uint Characteristics { get; set; }
    }

    /// <summary>
    /// CLI头（.NET特有的头部信息）
    /// </summary>
    class CLIHeader
    {
        public uint Cb { get; set; }
        public ushort MajorRuntimeVersion { get; set; }
        public ushort MinorRuntimeVersion { get; set; }
        public uint MetadataRVA { get; set; }
        public uint MetadataSize { get; set; }
        public uint Flags { get; set; }
        public uint EntryPointToken { get; set; }
        public uint ResourcesRVA { get; set; }
        public uint ResourcesSize { get; set; }
        public uint StrongNameSignatureRVA { get; set; }
        public uint StrongNameSignatureSize { get; set; }
    }

    /// <summary>
    /// 元数据根信息
    /// </summary>
    class MetadataRoot
    {
        public ushort MajorVersion { get; set; }
        public ushort MinorVersion { get; set; }
        public string Version { get; set; } = string.Empty;
        public Dictionary<string, MetadataStream> Streams { get; set; } = new Dictionary<string, MetadataStream>();
        public int StringIndexSize { get; set; }
        public int GuidIndexSize { get; set; }
        public int BlobIndexSize { get; set; }
        public int[] TableRowCounts { get; set; } = Array.Empty<int>();
        
        // Parsed metadata tables (45 tables total)
        public ModuleRow[] ModuleTable { get; set; } = Array.Empty<ModuleRow>();
        public TypeRefRow[] TypeRefTable { get; set; } = Array.Empty<TypeRefRow>();
        public TypeDefRow[] TypeDefTable { get; set; } = Array.Empty<TypeDefRow>();
        public FieldPtrRow[] FieldPtrTable { get; set; } = Array.Empty<FieldPtrRow>();
        public FieldRow[] FieldTable { get; set; } = Array.Empty<FieldRow>();
        public MethodPtrRow[] MethodPtrTable { get; set; } = Array.Empty<MethodPtrRow>();
        public MethodDefRow[] MethodDefTable { get; set; } = Array.Empty<MethodDefRow>();
        public ParamPtrRow[] ParamPtrTable { get; set; } = Array.Empty<ParamPtrRow>();
        public ParamRow[] ParamTable { get; set; } = Array.Empty<ParamRow>();
        public InterfaceImplRow[] InterfaceImplTable { get; set; } = Array.Empty<InterfaceImplRow>();
        public MemberRefRow[] MemberRefTable { get; set; } = Array.Empty<MemberRefRow>();
        public ConstantRow[] ConstantTable { get; set; } = Array.Empty<ConstantRow>();
        public CustomAttributeRow[] CustomAttributeTable { get; set; } = Array.Empty<CustomAttributeRow>();
        public FieldMarshalRow[] FieldMarshalTable { get; set; } = Array.Empty<FieldMarshalRow>();
        public DeclSecurityRow[] DeclSecurityTable { get; set; } = Array.Empty<DeclSecurityRow>();
        public ClassLayoutRow[] ClassLayoutTable { get; set; } = Array.Empty<ClassLayoutRow>();
        public FieldLayoutRow[] FieldLayoutTable { get; set; } = Array.Empty<FieldLayoutRow>();
        public StandAloneSigRow[] StandAloneSigTable { get; set; } = Array.Empty<StandAloneSigRow>();
        public EventMapRow[] EventMapTable { get; set; } = Array.Empty<EventMapRow>();
        public EventPtrRow[] EventPtrTable { get; set; } = Array.Empty<EventPtrRow>();
        public EventRow[] EventTable { get; set; } = Array.Empty<EventRow>();
        public PropertyMapRow[] PropertyMapTable { get; set; } = Array.Empty<PropertyMapRow>();
        public PropertyPtrRow[] PropertyPtrTable { get; set; } = Array.Empty<PropertyPtrRow>();
        public PropertyRow[] PropertyTable { get; set; } = Array.Empty<PropertyRow>();
        public MethodSemanticsRow[] MethodSemanticsTable { get; set; } = Array.Empty<MethodSemanticsRow>();
        public MethodImplRow[] MethodImplTable { get; set; } = Array.Empty<MethodImplRow>();
        public ModuleRefRow[] ModuleRefTable { get; set; } = Array.Empty<ModuleRefRow>();
        public TypeSpecRow[] TypeSpecTable { get; set; } = Array.Empty<TypeSpecRow>();
        public ImplMapRow[] ImplMapTable { get; set; } = Array.Empty<ImplMapRow>();
        public FieldRVARow[] FieldRVATable { get; set; } = Array.Empty<FieldRVARow>();
        public ENCLogRow[] ENCLogTable { get; set; } = Array.Empty<ENCLogRow>();
        public ENCMapRow[] ENCMapTable { get; set; } = Array.Empty<ENCMapRow>();
        public AssemblyRow[] AssemblyTable { get; set; } = Array.Empty<AssemblyRow>();
        public AssemblyProcessorRow[] AssemblyProcessorTable { get; set; } = Array.Empty<AssemblyProcessorRow>();
        public AssemblyOSRow[] AssemblyOSTable { get; set; } = Array.Empty<AssemblyOSRow>();
        public AssemblyRefRow[] AssemblyRefTable { get; set; } = Array.Empty<AssemblyRefRow>();
        public AssemblyRefProcessorRow[] AssemblyRefProcessorTable { get; set; } = Array.Empty<AssemblyRefProcessorRow>();
        public AssemblyRefOSRow[] AssemblyRefOSTable { get; set; } = Array.Empty<AssemblyRefOSRow>();
        public FileRow[] FileTable { get; set; } = Array.Empty<FileRow>();
        public ExportedTypeRow[] ExportedTypeTable { get; set; } = Array.Empty<ExportedTypeRow>();
        public ManifestResourceRow[] ManifestResourceTable { get; set; } = Array.Empty<ManifestResourceRow>();
        public NestedClassRow[] NestedClassTable { get; set; } = Array.Empty<NestedClassRow>();
        public GenericParamRow[] GenericParamTable { get; set; } = Array.Empty<GenericParamRow>();
        public MethodSpecRow[] MethodSpecTable { get; set; } = Array.Empty<MethodSpecRow>();
        public GenericParamConstraintRow[] GenericParamConstraintTable { get; set; } = Array.Empty<GenericParamConstraintRow>();
    }

    /// <summary>
    /// 元数据流信息
    /// </summary>
    class MetadataStream
    {
        public string Name { get; set; } = string.Empty;
        public uint Offset { get; set; }
        public uint Size { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }
}
