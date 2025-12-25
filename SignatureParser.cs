namespace ECMA335Printer
{
    /// <summary>
    /// Signature parser for Field and Method signatures from Blob heap
    /// </summary>
    public class SignatureParser
    {
        private byte[] _data;
        private int _position;

        public SignatureParser(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        /// <summary>
        /// Parse a field signature from blob data
        /// </summary>
        public FieldSignature ParseFieldSignature()
        {
            var sig = new FieldSignature();
            sig.RawData = _data;

            if (_data.Length == 0)
                return sig;

            sig.CallingConvention = (CallingConvention)ReadByte();
            
            // Skip custom modifiers if present
            SkipCustomModifiers();

            sig.FieldType = ParseType();
            return sig;
        }

        /// <summary>
        /// Parse a method signature from blob data
        /// </summary>
        public MethodSignature ParseMethodSignature()
        {
            var sig = new MethodSignature();
            sig.RawData = _data;

            if (_data.Length == 0)
                return sig;

            sig.CallingConvention = (CallingConvention)ReadByte();

            // If generic method, read generic param count
            if ((sig.CallingConvention & CallingConvention.GENERIC) != 0)
            {
                sig.GenericParamCount = ReadCompressedInt();
            }

            sig.ParamCount = ReadCompressedInt();

            // Skip custom modifiers on return type
            SkipCustomModifiers();

            sig.ReturnType = ParseType();

            // Parse parameters
            sig.Parameters = new SignatureType[sig.ParamCount];
            for (int i = 0; i < sig.ParamCount; i++)
            {
                // Skip custom modifiers on parameter
                SkipCustomModifiers();
                sig.Parameters[i] = ParseType();
            }

            return sig;
        }

        /// <summary>
        /// Parse a property signature from blob data
        /// </summary>
        public PropertySignature ParsePropertySignature()
        {
            var sig = new PropertySignature();
            sig.RawData = _data;

            if (_data.Length == 0)
                return sig;

            sig.CallingConvention = (CallingConvention)ReadByte();
            sig.ParamCount = ReadCompressedInt();

            // Skip custom modifiers
            SkipCustomModifiers();

            sig.PropertyType = ParseType();

            // Parse index parameters (for indexed properties)
            sig.Parameters = new SignatureType[sig.ParamCount];
            for (int i = 0; i < sig.ParamCount; i++)
            {
                SkipCustomModifiers();
                sig.Parameters[i] = ParseType();
            }

            return sig;
        }

        /// <summary>
        /// Parse a type from the signature
        /// </summary>
        private SignatureType ParseType()
        {
            if (_position >= _data.Length)
                return new SignatureType { ElementType = ElementType.VOID };

            var type = new SignatureType();
            var elementType = (ElementType)ReadByte();
            type.ElementType = elementType;

            switch (elementType)
            {
                case ElementType.VOID:
                case ElementType.BOOLEAN:
                case ElementType.CHAR:
                case ElementType.I1:
                case ElementType.U1:
                case ElementType.I2:
                case ElementType.U2:
                case ElementType.I4:
                case ElementType.U4:
                case ElementType.I8:
                case ElementType.U8:
                case ElementType.R4:
                case ElementType.R8:
                case ElementType.STRING:
                case ElementType.OBJECT:
                case ElementType.I:
                case ElementType.U:
                case ElementType.TYPEDBYREF:
                    // Simple types, no additional data
                    break;

                case ElementType.PTR:
                case ElementType.BYREF:
                case ElementType.SZARRAY:
                case ElementType.PINNED:
                    // Followed by another type
                    SkipCustomModifiers();
                    type.InnerType = ParseType();
                    break;

                case ElementType.VALUETYPE:
                case ElementType.CLASS:
                    // Followed by TypeDef or TypeRef token (compressed)
                    type.Token = (uint)ReadCompressedInt();
                    break;

                case ElementType.VAR:
                case ElementType.MVAR:
                    // Generic parameter number
                    type.GenericParamNumber = ReadCompressedInt();
                    break;

                case ElementType.ARRAY:
                    // Multi-dimensional array
                    type.InnerType = ParseType();
                    type.ArrayShape = ParseArrayShape();
                    break;

                case ElementType.GENERICINST:
                    // Generic type instantiation
                    type.InnerType = ParseType(); // The generic type (CLASS or VALUETYPE)
                    int genArgCount = ReadCompressedInt();
                    type.GenericArgs = new SignatureType[genArgCount];
                    for (int i = 0; i < genArgCount; i++)
                    {
                        type.GenericArgs[i] = ParseType();
                    }
                    break;

                case ElementType.FNPTR:
                    // Function pointer - skip for now (complex)
                    // Would need to parse a full method signature
                    break;

                case ElementType.CMOD_REQD:
                case ElementType.CMOD_OPT:
                    // Custom modifier - read token and continue with actual type
                    type.Token = (uint)ReadCompressedInt();
                    type.InnerType = ParseType();
                    break;

                case ElementType.SENTINEL:
                    // Marks the start of vararg parameters
                    break;

                default:
                    // Unknown type
                    break;
            }

            return type;
        }

        /// <summary>
        /// Parse array shape for multi-dimensional arrays
        /// </summary>
        private ArrayShape ParseArrayShape()
        {
            var shape = new ArrayShape();
            shape.Rank = ReadCompressedInt();

            int numSizes = ReadCompressedInt();
            shape.Sizes = new int[numSizes];
            for (int i = 0; i < numSizes; i++)
            {
                shape.Sizes[i] = ReadCompressedInt();
            }

            int numLoBounds = ReadCompressedInt();
            shape.LowerBounds = new int[numLoBounds];
            for (int i = 0; i < numLoBounds; i++)
            {
                shape.LowerBounds[i] = ReadCompressedInt();
            }

            return shape;
        }

        /// <summary>
        /// Skip custom modifiers (CMOD_REQD, CMOD_OPT)
        /// </summary>
        private void SkipCustomModifiers()
        {
            while (_position < _data.Length)
            {
                var b = _data[_position];
                if (b == (byte)ElementType.CMOD_REQD || b == (byte)ElementType.CMOD_OPT)
                {
                    _position++; // Skip the modifier byte
                    ReadCompressedInt(); // Skip the token
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Read a single byte
        /// </summary>
        private byte ReadByte()
        {
            if (_position >= _data.Length)
                return 0;
            return _data[_position++];
        }

        /// <summary>
        /// Read a compressed integer (ECMA-335 II.23.2)
        /// </summary>
        private int ReadCompressedInt()
        {
            if (_position >= _data.Length)
                return 0;

            byte first = _data[_position++];

            if ((first & 0x80) == 0)
            {
                // 1-byte encoding: 0xxxxxxx
                return first;
            }
            else if ((first & 0xC0) == 0x80)
            {
                // 2-byte encoding: 10xxxxxx xxxxxxxx
                if (_position >= _data.Length)
                    return 0;
                byte second = _data[_position++];
                return ((first & 0x3F) << 8) | second;
            }
            else if ((first & 0xE0) == 0xC0)
            {
                // 4-byte encoding: 110xxxxx xxxxxxxx xxxxxxxx xxxxxxxx
                if (_position + 2 >= _data.Length)
                    return 0;
                byte b2 = _data[_position++];
                byte b3 = _data[_position++];
                byte b4 = _data[_position++];
                return ((first & 0x1F) << 24) | (b2 << 16) | (b3 << 8) | b4;
            }

            return 0;
        }
    }
}
