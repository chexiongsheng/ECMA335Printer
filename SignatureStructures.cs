namespace ECMA335Printer
{
    /// <summary>
    /// Element type encoding in signatures (ECMA-335 II.23.1.16)
    /// </summary>
    public enum ElementType : byte
    {
        END = 0x00,             // Marks end of a list
        VOID = 0x01,
        BOOLEAN = 0x02,
        CHAR = 0x03,
        I1 = 0x04,              // sbyte
        U1 = 0x05,              // byte
        I2 = 0x06,              // short
        U2 = 0x07,              // ushort
        I4 = 0x08,              // int
        U4 = 0x09,              // uint
        I8 = 0x0A,              // long
        U8 = 0x0B,              // ulong
        R4 = 0x0C,              // float
        R8 = 0x0D,              // double
        STRING = 0x0E,
        PTR = 0x0F,             // Followed by type
        BYREF = 0x10,           // Followed by type
        VALUETYPE = 0x11,       // Followed by TypeDef or TypeRef token
        CLASS = 0x12,           // Followed by TypeDef or TypeRef token
        VAR = 0x13,             // Generic parameter in a generic type definition
        ARRAY = 0x14,           // Multi-dimensional array
        GENERICINST = 0x15,     // Generic type instantiation
        TYPEDBYREF = 0x16,
        I = 0x18,               // native int
        U = 0x19,               // native uint
        FNPTR = 0x1B,           // Function pointer
        OBJECT = 0x1C,
        SZARRAY = 0x1D,         // Single-dimension array with 0 lower bound
        MVAR = 0x1E,            // Generic parameter in a generic method definition
        CMOD_REQD = 0x1F,       // Required custom modifier
        CMOD_OPT = 0x20,        // Optional custom modifier
        INTERNAL = 0x21,
        MODIFIER = 0x40,        // Or'd with following element types
        SENTINEL = 0x41,        // Sentinel for vararg method signature
        PINNED = 0x45,          // Local variable is pinned
    }

    /// <summary>
    /// Calling convention flags (ECMA-335 II.23.2.3)
    /// </summary>
    [Flags]
    public enum CallingConvention : byte
    {
        DEFAULT = 0x00,         // Default calling convention
        C = 0x01,               // C calling convention
        STDCALL = 0x02,         // StdCall calling convention
        THISCALL = 0x03,        // ThisCall calling convention
        FASTCALL = 0x04,        // FastCall calling convention
        VARARG = 0x05,          // Variable argument list
        FIELD = 0x06,           // Field signature
        LOCAL_SIG = 0x07,       // Local variable signature
        PROPERTY = 0x08,        // Property signature
        UNMANAGED = 0x09,       // Unmanaged calling convention
        GENERICINST = 0x0A,     // Generic method instantiation
        HASTHIS = 0x20,         // Has 'this' pointer
        EXPLICITTHIS = 0x40,    // 'this' pointer is explicit
        GENERIC = 0x10,         // Generic method
    }

    /// <summary>
    /// Represents a type in a signature
    /// </summary>
    public class SignatureType
    {
        public ElementType ElementType { get; set; }
        public uint Token { get; set; }                 // For VALUETYPE, CLASS, etc.
        public SignatureType? InnerType { get; set; }   // For PTR, BYREF, SZARRAY, etc.
        public SignatureType[]? GenericArgs { get; set; } // For GENERICINST
        public ArrayShape? ArrayShape { get; set; }     // For ARRAY
        public int GenericParamNumber { get; set; }     // For VAR, MVAR

        public override string ToString()
        {
            return ElementType switch
            {
                ElementType.VOID => "void",
                ElementType.BOOLEAN => "bool",
                ElementType.CHAR => "char",
                ElementType.I1 => "sbyte",
                ElementType.U1 => "byte",
                ElementType.I2 => "short",
                ElementType.U2 => "ushort",
                ElementType.I4 => "int",
                ElementType.U4 => "uint",
                ElementType.I8 => "long",
                ElementType.U8 => "ulong",
                ElementType.R4 => "float",
                ElementType.R8 => "double",
                ElementType.STRING => "string",
                ElementType.OBJECT => "object",
                ElementType.I => "nint",
                ElementType.U => "nuint",
                ElementType.PTR => $"{InnerType}*",
                ElementType.BYREF => $"ref {InnerType}",
                ElementType.SZARRAY => $"{InnerType}[]",
                ElementType.VALUETYPE => $"valuetype(0x{Token:X8})",
                ElementType.CLASS => $"class(0x{Token:X8})",
                ElementType.VAR => $"!{GenericParamNumber}",
                ElementType.MVAR => $"!!{GenericParamNumber}",
                ElementType.GENERICINST => $"{InnerType}<{string.Join(", ", (IEnumerable<SignatureType>)(GenericArgs ?? Array.Empty<SignatureType>()))}>",
                _ => $"UNKNOWN(0x{(byte)ElementType:X2})"
            };
        }
    }

    /// <summary>
    /// Array shape for multi-dimensional arrays
    /// </summary>
    public class ArrayShape
    {
        public int Rank { get; set; }
        public int[] Sizes { get; set; } = Array.Empty<int>();
        public int[] LowerBounds { get; set; } = Array.Empty<int>();
    }

    /// <summary>
    /// Field signature (ECMA-335 II.23.2.4)
    /// Format: FIELD CustomMod* Type
    /// </summary>
    public class FieldSignature
    {
        public CallingConvention CallingConvention { get; set; }  // Should be FIELD (0x06)
        public SignatureType FieldType { get; set; } = new SignatureType();
        public byte[] RawData { get; set; } = Array.Empty<byte>();

        public override string ToString()
        {
            return $"Field: {FieldType}";
        }
    }

    /// <summary>
    /// Method signature (ECMA-335 II.23.2.1)
    /// Format: [HASTHIS] [EXPLICITTHIS] [DEFAULT|VARARG|GENERIC] ParamCount RetType Param*
    /// </summary>
    public class MethodSignature
    {
        public CallingConvention CallingConvention { get; set; }
        public int GenericParamCount { get; set; }      // For generic methods
        public int ParamCount { get; set; }
        public SignatureType ReturnType { get; set; } = new SignatureType();
        public SignatureType[] Parameters { get; set; } = Array.Empty<SignatureType>();
        public byte[] RawData { get; set; } = Array.Empty<byte>();

        public bool HasThis => (CallingConvention & CallingConvention.HASTHIS) != 0;
        public bool ExplicitThis => (CallingConvention & CallingConvention.EXPLICITTHIS) != 0;
        public bool IsGeneric => (CallingConvention & CallingConvention.GENERIC) != 0;
        public bool IsVarArg => (CallingConvention & (CallingConvention)0x0F) == CallingConvention.VARARG;

        public override string ToString()
        {
            var flags = new List<string>();
            if (HasThis) flags.Add("instance");
            if (IsGeneric) flags.Add($"generic<{GenericParamCount}>");
            if (IsVarArg) flags.Add("vararg");

            var flagStr = flags.Count > 0 ? string.Join(" ", flags) + " " : "";
            var paramStr = string.Join(", ", Parameters.Select(p => p.ToString()));
            return $"{flagStr}{ReturnType} ({paramStr})";
        }
    }

    /// <summary>
    /// Property signature (ECMA-335 II.23.2.5)
    /// Format: PROPERTY ParamCount CustomMod* Type Param*
    /// </summary>
    public class PropertySignature
    {
        public CallingConvention CallingConvention { get; set; }  // Should be PROPERTY (0x08)
        public int ParamCount { get; set; }
        public SignatureType PropertyType { get; set; } = new SignatureType();
        public SignatureType[] Parameters { get; set; } = Array.Empty<SignatureType>();
        public byte[] RawData { get; set; } = Array.Empty<byte>();

        public bool HasThis => (CallingConvention & CallingConvention.HASTHIS) != 0;

        public override string ToString()
        {
            var paramStr = Parameters.Length > 0 ? $"[{string.Join(", ", Parameters.Select(p => p.ToString()))}]" : "";
            return $"Property: {PropertyType}{paramStr}";
        }
    }
}
