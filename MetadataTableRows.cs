namespace ECMA335Printer
{
    /// <summary>
    /// Metadata table row structures according to ECMA-335 specification
    /// Each table is an array of these row structures
    /// </summary>

    #region Table 0x00: Module
    /// <summary>
    /// Module table (0x00) - Contains a single row describing the current module
    /// </summary>
    class ModuleRow
    {
        public ushort Generation { get; set; }          // 2-byte value, reserved (should be 0)
        public uint Name { get; set; }                  // Index into String heap
        public uint Mvid { get; set; }                  // Index into GUID heap (Module Version ID)
        public uint EncId { get; set; }                 // Index into GUID heap, reserved (should be 0)
        public uint EncBaseId { get; set; }             // Index into GUID heap, reserved (should be 0)
    }
    #endregion

    #region Table 0x01: TypeRef
    /// <summary>
    /// TypeRef table (0x01) - References to types defined in other modules
    /// </summary>
    class TypeRefRow
    {
        public uint ResolutionScope { get; set; }       // Coded index: ResolutionScope (Module, ModuleRef, AssemblyRef, TypeRef)
        public uint TypeName { get; set; }              // Index into String heap
        public uint TypeNamespace { get; set; }         // Index into String heap
    }
    #endregion

    #region Table 0x02: TypeDef
    /// <summary>
    /// TypeDef table (0x02) - Definitions of types in the current module
    /// </summary>
    class TypeDefRow
    {
        public uint Flags { get; set; }                 // 4-byte bitmask of TypeAttributes
        public uint TypeName { get; set; }              // Index into String heap
        public uint TypeNamespace { get; set; }         // Index into String heap
        public uint Extends { get; set; }               // Coded index: TypeDefOrRef (TypeDef, TypeRef, TypeSpec)
        public uint FieldList { get; set; }             // Index into Field table (first field of this type)
        public uint MethodList { get; set; }            // Index into MethodDef table (first method of this type)
    }
    #endregion

    #region Table 0x04: Field
    /// <summary>
    /// Field table (0x04) - Field definitions
    /// </summary>
    class FieldRow
    {
        public ushort Flags { get; set; }               // 2-byte bitmask of FieldAttributes
        public uint Name { get; set; }                  // Index into String heap
        public uint Signature { get; set; }             // Index into Blob heap (field signature)
        
        // Resolved data
        public string? NameString { get; set; }         // Resolved name from String heap
        public FieldSignature? ParsedSignature { get; set; }  // Parsed signature from Blob heap
    }
    #endregion

    #region Table 0x06: MethodDef
    /// <summary>
    /// MethodDef table (0x06) - Method definitions
    /// </summary>
    class MethodDefRow
    {
        public uint RVA { get; set; }                   // 4-byte RVA of method body
        public ushort ImplFlags { get; set; }           // 2-byte bitmask of MethodImplAttributes
        public ushort Flags { get; set; }               // 2-byte bitmask of MethodAttributes
        public uint Name { get; set; }                  // Index into String heap
        public uint Signature { get; set; }             // Index into Blob heap (method signature)
        public uint ParamList { get; set; }             // Index into Param table (first parameter)
        
        // Resolved data
        public string? NameString { get; set; }         // Resolved name from String heap
        public MethodSignature? ParsedSignature { get; set; }  // Parsed signature from Blob heap
        public MethodBody? MethodBody { get; set; }     // Parsed method body from RVA
    }
    #endregion

    #region Table 0x08: Param
    /// <summary>
    /// Param table (0x08) - Parameter definitions
    /// </summary>
    class ParamRow
    {
        public ushort Flags { get; set; }               // 2-byte bitmask of ParamAttributes
        public ushort Sequence { get; set; }            // 2-byte constant (0 for return value, 1+ for parameters)
        public uint Name { get; set; }                  // Index into String heap
    }
    #endregion

    #region Table 0x09: InterfaceImpl
    /// <summary>
    /// InterfaceImpl table (0x09) - Interface implementations
    /// </summary>
    class InterfaceImplRow
    {
        public uint Class { get; set; }                 // Index into TypeDef table
        public uint Interface { get; set; }             // Coded index: TypeDefOrRef (TypeDef, TypeRef, TypeSpec)
    }
    #endregion

    #region Table 0x0A: MemberRef
    /// <summary>
    /// MemberRef table (0x0A) - References to fields and methods
    /// </summary>
    class MemberRefRow
    {
        public uint Class { get; set; }                 // Coded index: MemberRefParent (TypeDef, TypeRef, ModuleRef, MethodDef, TypeSpec)
        public uint Name { get; set; }                  // Index into String heap
        public uint Signature { get; set; }             // Index into Blob heap
    }
    #endregion

    #region Table 0x0B: Constant
    /// <summary>
    /// Constant table (0x0B) - Constant values for fields, parameters, and properties
    /// </summary>
    class ConstantRow
    {
        public byte Type { get; set; }                  // 1-byte constant (element type)
        public byte Padding { get; set; }               // 1-byte padding (should be 0)
        public uint Parent { get; set; }                // Coded index: HasConstant (Field, Param, Property)
        public uint Value { get; set; }                 // Index into Blob heap
    }
    #endregion

    #region Table 0x0C: CustomAttribute
    /// <summary>
    /// CustomAttribute table (0x0C) - Custom attributes
    /// </summary>
    class CustomAttributeRow
    {
        public uint Parent { get; set; }                // Coded index: HasCustomAttribute (any metadata table)
        public uint Type { get; set; }                  // Coded index: CustomAttributeType (MethodDef, MemberRef)
        public uint Value { get; set; }                 // Index into Blob heap
    }
    #endregion

    #region Table 0x0D: FieldMarshal
    /// <summary>
    /// FieldMarshal table (0x0D) - Marshalling information for fields and parameters
    /// </summary>
    class FieldMarshalRow
    {
        public uint Parent { get; set; }                // Coded index: HasFieldMarshal (Field, Param)
        public uint NativeType { get; set; }            // Index into Blob heap
    }
    #endregion

    #region Table 0x0E: DeclSecurity
    /// <summary>
    /// DeclSecurity table (0x0E) - Security declarations
    /// </summary>
    class DeclSecurityRow
    {
        public ushort Action { get; set; }              // 2-byte value (SecurityAction)
        public uint Parent { get; set; }                // Coded index: HasDeclSecurity (TypeDef, MethodDef, Assembly)
        public uint PermissionSet { get; set; }         // Index into Blob heap
    }
    #endregion

    #region Table 0x0F: ClassLayout
    /// <summary>
    /// ClassLayout table (0x0F) - Layout information for types
    /// </summary>
    class ClassLayoutRow
    {
        public ushort PackingSize { get; set; }         // 2-byte constant
        public uint ClassSize { get; set; }             // 4-byte constant
        public uint Parent { get; set; }                // Index into TypeDef table
    }
    #endregion

    #region Table 0x10: FieldLayout
    /// <summary>
    /// FieldLayout table (0x10) - Layout information for fields
    /// </summary>
    class FieldLayoutRow
    {
        public uint Offset { get; set; }                // 4-byte constant
        public uint Field { get; set; }                 // Index into Field table
    }
    #endregion

    #region Table 0x11: StandAloneSig
    /// <summary>
    /// StandAloneSig table (0x11) - Standalone signatures (for calli instruction)
    /// </summary>
    class StandAloneSigRow
    {
        public uint Signature { get; set; }             // Index into Blob heap
    }
    #endregion

    #region Table 0x12: EventMap
    /// <summary>
    /// EventMap table (0x12) - Maps types to their events
    /// </summary>
    class EventMapRow
    {
        public uint Parent { get; set; }                // Index into TypeDef table
        public uint EventList { get; set; }             // Index into Event table
    }
    #endregion

    #region Table 0x14: Event
    /// <summary>
    /// Event table (0x14) - Event definitions
    /// </summary>
    class EventRow
    {
        public ushort EventFlags { get; set; }          // 2-byte bitmask of EventAttributes
        public uint Name { get; set; }                  // Index into String heap
        public uint EventType { get; set; }             // Coded index: TypeDefOrRef (TypeDef, TypeRef, TypeSpec)
    }
    #endregion

    #region Table 0x15: PropertyMap
    /// <summary>
    /// PropertyMap table (0x15) - Maps types to their properties
    /// </summary>
    class PropertyMapRow
    {
        public uint Parent { get; set; }                // Index into TypeDef table
        public uint PropertyList { get; set; }          // Index into Property table
    }
    #endregion

    #region Table 0x17: Property
    /// <summary>
    /// Property table (0x17) - Property definitions
    /// </summary>
    class PropertyRow
    {
        public ushort Flags { get; set; }               // 2-byte bitmask of PropertyAttributes
        public uint Name { get; set; }                  // Index into String heap
        public uint Type { get; set; }                  // Index into Blob heap (property signature)
    }
    #endregion

    #region Table 0x18: MethodSemantics
    /// <summary>
    /// MethodSemantics table (0x18) - Links methods to properties and events
    /// </summary>
    class MethodSemanticsRow
    {
        public ushort Semantics { get; set; }           // 2-byte bitmask of MethodSemanticsAttributes
        public uint Method { get; set; }                // Index into MethodDef table
        public uint Association { get; set; }           // Coded index: HasSemantics (Event, Property)
    }
    #endregion

    #region Table 0x19: MethodImpl
    /// <summary>
    /// MethodImpl table (0x19) - Method implementation overrides
    /// </summary>
    class MethodImplRow
    {
        public uint Class { get; set; }                 // Index into TypeDef table
        public uint MethodBody { get; set; }            // Coded index: MethodDefOrRef (MethodDef, MemberRef)
        public uint MethodDeclaration { get; set; }     // Coded index: MethodDefOrRef (MethodDef, MemberRef)
    }
    #endregion

    #region Table 0x1A: ModuleRef
    /// <summary>
    /// ModuleRef table (0x1A) - References to external modules
    /// </summary>
    class ModuleRefRow
    {
        public uint Name { get; set; }                  // Index into String heap
    }
    #endregion

    #region Table 0x1B: TypeSpec
    /// <summary>
    /// TypeSpec table (0x1B) - Type specifications (generic instantiations, arrays, etc.)
    /// </summary>
    class TypeSpecRow
    {
        public uint Signature { get; set; }             // Index into Blob heap
    }
    #endregion

    #region Table 0x1C: ImplMap
    /// <summary>
    /// ImplMap table (0x1C) - P/Invoke information
    /// </summary>
    class ImplMapRow
    {
        public ushort MappingFlags { get; set; }        // 2-byte bitmask of PInvokeAttributes
        public uint MemberForwarded { get; set; }       // Coded index: MemberForwarded (Field, MethodDef)
        public uint ImportName { get; set; }            // Index into String heap
        public uint ImportScope { get; set; }           // Index into ModuleRef table
    }
    #endregion

    #region Table 0x1D: FieldRVA
    /// <summary>
    /// FieldRVA table (0x1D) - RVA for fields with initial data
    /// </summary>
    class FieldRVARow
    {
        public uint RVA { get; set; }                   // 4-byte RVA
        public uint Field { get; set; }                 // Index into Field table
    }
    #endregion

    #region Table 0x1E: ENCLog
    /// <summary>
    /// ENCLog table (0x1E) - Edit and Continue log (debugging)
    /// </summary>
    class ENCLogRow
    {
        public uint Token { get; set; }                 // 4-byte token
        public uint FuncCode { get; set; }              // 4-byte function code
    }
    #endregion

    #region Table 0x1F: ENCMap
    /// <summary>
    /// ENCMap table (0x1F) - Edit and Continue map (debugging)
    /// </summary>
    class ENCMapRow
    {
        public uint Token { get; set; }                 // 4-byte token
    }
    #endregion

    #region Table 0x20: Assembly
    /// <summary>
    /// Assembly table (0x20) - Assembly definition (single row)
    /// </summary>
    class AssemblyRow
    {
        public uint HashAlgId { get; set; }             // 4-byte constant (AssemblyHashAlgorithm)
        public ushort MajorVersion { get; set; }        // 2-byte constant
        public ushort MinorVersion { get; set; }        // 2-byte constant
        public ushort BuildNumber { get; set; }         // 2-byte constant
        public ushort RevisionNumber { get; set; }      // 2-byte constant
        public uint Flags { get; set; }                 // 4-byte bitmask of AssemblyFlags
        public uint PublicKey { get; set; }             // Index into Blob heap
        public uint Name { get; set; }                  // Index into String heap
        public uint Culture { get; set; }               // Index into String heap
    }
    #endregion

    #region Table 0x21: AssemblyProcessor
    /// <summary>
    /// AssemblyProcessor table (0x21) - Processor information (deprecated)
    /// </summary>
    class AssemblyProcessorRow
    {
        public uint Processor { get; set; }             // 4-byte constant
    }
    #endregion

    #region Table 0x22: AssemblyOS
    /// <summary>
    /// AssemblyOS table (0x22) - OS information (deprecated)
    /// </summary>
    class AssemblyOSRow
    {
        public uint OSPlatformID { get; set; }          // 4-byte constant
        public uint OSMajorVersion { get; set; }        // 4-byte constant
        public uint OSMinorVersion { get; set; }        // 4-byte constant
    }
    #endregion

    #region Table 0x23: AssemblyRef
    /// <summary>
    /// AssemblyRef table (0x23) - References to external assemblies
    /// </summary>
    class AssemblyRefRow
    {
        public ushort MajorVersion { get; set; }        // 2-byte constant
        public ushort MinorVersion { get; set; }        // 2-byte constant
        public ushort BuildNumber { get; set; }         // 2-byte constant
        public ushort RevisionNumber { get; set; }      // 2-byte constant
        public uint Flags { get; set; }                 // 4-byte bitmask of AssemblyFlags
        public uint PublicKeyOrToken { get; set; }      // Index into Blob heap
        public uint Name { get; set; }                  // Index into String heap
        public uint Culture { get; set; }               // Index into String heap
        public uint HashValue { get; set; }             // Index into Blob heap
    }
    #endregion

    #region Table 0x24: AssemblyRefProcessor
    /// <summary>
    /// AssemblyRefProcessor table (0x24) - Processor information for assembly references (deprecated)
    /// </summary>
    class AssemblyRefProcessorRow
    {
        public uint Processor { get; set; }             // 4-byte constant
        public uint AssemblyRef { get; set; }           // Index into AssemblyRef table
    }
    #endregion

    #region Table 0x25: AssemblyRefOS
    /// <summary>
    /// AssemblyRefOS table (0x25) - OS information for assembly references (deprecated)
    /// </summary>
    class AssemblyRefOSRow
    {
        public uint OSPlatformID { get; set; }          // 4-byte constant
        public uint OSMajorVersion { get; set; }        // 4-byte constant
        public uint OSMinorVersion { get; set; }        // 4-byte constant
        public uint AssemblyRef { get; set; }           // Index into AssemblyRef table
    }
    #endregion

    #region Table 0x26: File
    /// <summary>
    /// File table (0x26) - Files in the assembly (multi-file assemblies)
    /// </summary>
    class FileRow
    {
        public uint Flags { get; set; }                 // 4-byte bitmask of FileAttributes
        public uint Name { get; set; }                  // Index into String heap
        public uint HashValue { get; set; }             // Index into Blob heap
    }
    #endregion

    #region Table 0x27: ExportedType
    /// <summary>
    /// ExportedType table (0x27) - Types exported from the assembly
    /// </summary>
    class ExportedTypeRow
    {
        public uint Flags { get; set; }                 // 4-byte bitmask of TypeAttributes
        public uint TypeDefId { get; set; }             // 4-byte index into TypeDef table (in another module)
        public uint TypeName { get; set; }              // Index into String heap
        public uint TypeNamespace { get; set; }         // Index into String heap
        public uint Implementation { get; set; }        // Coded index: Implementation (File, AssemblyRef, ExportedType)
    }
    #endregion

    #region Table 0x28: ManifestResource
    /// <summary>
    /// ManifestResource table (0x28) - Manifest resources
    /// </summary>
    class ManifestResourceRow
    {
        public uint Offset { get; set; }                // 4-byte constant
        public uint Flags { get; set; }                 // 4-byte bitmask of ManifestResourceAttributes
        public uint Name { get; set; }                  // Index into String heap
        public uint Implementation { get; set; }        // Coded index: Implementation (File, AssemblyRef, null)
    }
    #endregion

    #region Table 0x29: NestedClass
    /// <summary>
    /// NestedClass table (0x29) - Nested class relationships
    /// </summary>
    class NestedClassRow
    {
        public uint NestedClass { get; set; }           // Index into TypeDef table
        public uint EnclosingClass { get; set; }        // Index into TypeDef table
    }
    #endregion

    #region Table 0x2A: GenericParam
    /// <summary>
    /// GenericParam table (0x2A) - Generic parameters for types and methods
    /// </summary>
    class GenericParamRow
    {
        public ushort Number { get; set; }              // 2-byte index of generic parameter
        public ushort Flags { get; set; }               // 2-byte bitmask of GenericParamAttributes
        public uint Owner { get; set; }                 // Coded index: TypeOrMethodDef (TypeDef, MethodDef)
        public uint Name { get; set; }                  // Index into String heap
    }
    #endregion

    #region Table 0x2B: MethodSpec
    /// <summary>
    /// MethodSpec table (0x2B) - Generic method instantiations
    /// </summary>
    class MethodSpecRow
    {
        public uint Method { get; set; }                // Coded index: MethodDefOrRef (MethodDef, MemberRef)
        public uint Instantiation { get; set; }         // Index into Blob heap
    }
    #endregion

    #region Table 0x2C: GenericParamConstraint
    /// <summary>
    /// GenericParamConstraint table (0x2C) - Constraints on generic parameters
    /// </summary>
    class GenericParamConstraintRow
    {
        public uint Owner { get; set; }                 // Index into GenericParam table
        public uint Constraint { get; set; }            // Coded index: TypeDefOrRef (TypeDef, TypeRef, TypeSpec)
    }
    #endregion

    #region Pointer Tables (0x03, 0x05, 0x07, 0x13, 0x16)
    /// <summary>
    /// Field_Ptr table (0x03) - Indirection for fields (rarely used)
    /// </summary>
    class FieldPtrRow
    {
        public uint Field { get; set; }                 // Index into Field table
    }

    /// <summary>
    /// Method_Ptr table (0x05) - Indirection for methods (rarely used)
    /// </summary>
    class MethodPtrRow
    {
        public uint Method { get; set; }                // Index into MethodDef table
    }

    /// <summary>
    /// Param_Ptr table (0x07) - Indirection for parameters (rarely used)
    /// </summary>
    class ParamPtrRow
    {
        public uint Param { get; set; }                 // Index into Param table
    }

    /// <summary>
    /// Event_Ptr table (0x13) - Indirection for events (rarely used)
    /// </summary>
    class EventPtrRow
    {
        public uint Event { get; set; }                 // Index into Event table
    }

    /// <summary>
    /// Property_Ptr table (0x16) - Indirection for properties (rarely used)
    /// </summary>
    class PropertyPtrRow
    {
        public uint Property { get; set; }              // Index into Property table
    }
    #endregion

    #region Method Body Structures
    /// <summary>
    /// Method body structure containing IL code
    /// </summary>
    class MethodBody
    {
        public bool IsTiny { get; set; }                // True if tiny format, false if fat format
        public ushort MaxStack { get; set; }            // Maximum stack size
        public uint CodeSize { get; set; }              // Size of IL code in bytes
        public uint LocalVarSigTok { get; set; }        // Token for local variable signature (StandAloneSig table)
        public byte[] ILCode { get; set; } = Array.Empty<byte>();  // IL instructions
        public ExceptionHandlingClause[] ExceptionClauses { get; set; } = Array.Empty<ExceptionHandlingClause>();
    }

    /// <summary>
    /// Exception handling clause (try-catch-finally)
    /// </summary>
    class ExceptionHandlingClause
    {
        public uint Flags { get; set; }                 // Exception clause flags
        public uint TryOffset { get; set; }             // Offset of try block
        public uint TryLength { get; set; }             // Length of try block
        public uint HandlerOffset { get; set; }         // Offset of handler block
        public uint HandlerLength { get; set; }         // Length of handler block
        public uint ClassTokenOrFilterOffset { get; set; }  // Exception type token or filter offset
    }
    #endregion
}
