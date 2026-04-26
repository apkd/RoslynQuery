using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Cysharp.Text;
using Microsoft.CodeAnalysis;
using static System.StringComparison;

namespace RoslynQuery;

static class IlViewer
{
    static readonly OpCode[] oneByteOpCodes = new OpCode[0x100];
    static readonly OpCode[] twoByteOpCodes = new OpCode[0x100];

    static IlViewer()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
                continue;

            var value = unchecked((ushort)opCode.Value);
            if (value < 0x100)
                oneByteOpCodes[value] = opCode;
            else if ((value & 0xff00) is 0xfe00)
                twoByteOpCodes[value & 0xff] = opCode;
        }
    }

    public static async Task<ViewIlResponse> ViewAsync(Project project, ISymbol symbol, bool compact, CancellationToken ct)
    {
        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return new() { Error = "The project did not produce a compilation." };

        await using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream, cancellationToken: ct);
        if (!emitResult.Success)
        {
            return new()
            {
                Error = "The project could not be compiled, so IL is unavailable.",
                EmitDiagnostics = emitResult.Diagnostics
                    .AsValueEnumerable()
                    .Where(static x => x.Severity is DiagnosticSeverity.Error)
                    .Select(static x => x.ToString())
                    .Take(20)
                    .ToArray(),
            };
        }

        peStream.Position = 0;
        using var peReader = new PEReader(peStream);
        var reader = peReader.GetMetadataReader();
        var options = new IlFormatOptions(compact);
        var typeSymbol = symbol.ContainingType;
        if (typeSymbol is null)
            return new() { Error = "The symbol is not declared on a type." };

        var typeHandle = FindType(reader, typeSymbol);
        if (typeHandle.IsNil)
            return new() { Error = "The symbol's containing type was not found in emitted metadata." };

        var methods = symbol switch
        {
            IMethodSymbol methodSymbol     => BuildMethodInfos(reader, peReader, typeHandle, methodSymbol, options),
            IPropertySymbol propertySymbol => BuildPropertyMethodInfos(reader, peReader, typeHandle, propertySymbol, options),
            _                              => [],
        };

        return methods.Length is 0
            ? new() { Error = "No emitted method body was found for the symbol." }
            : new() { Success = true, Methods = methods };
    }

    public static ViewIlResponse ViewMetadata(ResolvedSymbol resolved, string? assemblyPath, bool compact, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (assemblyPath is not { Length: > 0 })
            return new() { Error = "The symbol's metadata assembly path is unavailable, so IL is unavailable." };

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            var reader = peReader.GetMetadataReader();
            var options = new IlFormatOptions(compact);
            var typeSymbol = resolved.Symbol.ContainingType;
            if (typeSymbol is null)
                return new() { Error = "The symbol is not declared on a type." };

            var typeHandle = FindType(reader, typeSymbol);
            if (typeHandle.IsNil)
                return new() { Error = "The symbol's containing type was not found in metadata." };

            var methods = resolved.Symbol switch
            {
                IMethodSymbol methodSymbol     => BuildMethodInfos(reader, peReader, typeHandle, methodSymbol, options),
                IPropertySymbol propertySymbol => BuildPropertyMethodInfos(reader, peReader, typeHandle, propertySymbol, options),
                _                              => [],
            };

            return methods.Length is 0
                ? new() { Error = "No metadata method body was found for the symbol." }
                : new() { Success = true, Methods = methods };
        }
        catch (FileNotFoundException exception)
        {
            return new() { Error = $"The metadata assembly could not be found: {exception.Message}" };
        }
        catch (IOException exception)
        {
            return new() { Error = $"The metadata assembly could not be read: {exception.Message}" };
        }
        catch (BadImageFormatException exception)
        {
            return new() { Error = $"The metadata assembly is not a valid PE image: {exception.Message}" };
        }
    }

    static IlMethodInfo[] BuildMethodInfos(
        MetadataReader reader,
        PEReader peReader,
        TypeDefinitionHandle typeHandle,
        IMethodSymbol methodSymbol,
        IlFormatOptions options
    )
    {
        var methodHandle = FindMethod(reader, typeHandle, GetMethodMetadataName(methodSymbol), methodSymbol.Parameters.Length, methodSymbol.TypeParameters.Length);
        return methodHandle.IsNil
            ? []
            : [BuildMethodInfo(reader, peReader, methodHandle, SymbolText.GetDisplaySignature(methodSymbol), options)];
    }

    static IlMethodInfo[] BuildPropertyMethodInfos(
        MetadataReader reader,
        PEReader peReader,
        TypeDefinitionHandle typeHandle,
        IPropertySymbol propertySymbol,
        IlFormatOptions options
    )
    {
        var propertyHandle = FindProperty(reader, typeHandle, propertySymbol);
        if (propertyHandle.IsNil)
            return [];

        var accessors = reader.GetPropertyDefinition(propertyHandle).GetAccessors();
        var methods = new List<IlMethodInfo>(2);
        if (!accessors.Getter.IsNil)
            methods.Add(BuildMethodInfo(reader, peReader, accessors.Getter, $"{SymbolText.GetDisplaySignature(propertySymbol)}.get", options));

        if (!accessors.Setter.IsNil)
            methods.Add(BuildMethodInfo(reader, peReader, accessors.Setter, $"{SymbolText.GetDisplaySignature(propertySymbol)}.set", options));

        return [.. methods];
    }

    static TypeDefinitionHandle FindType(MetadataReader reader, INamedTypeSymbol typeSymbol)
    {
        var expectedName = GetTypeMetadataFullName(typeSymbol);
        foreach (var handle in reader.TypeDefinitions)
            if (string.Equals(GetTypeMetadataFullName(reader, handle), expectedName, Ordinal))
                return handle;

        return default;
    }

    static MethodDefinitionHandle FindMethod(
        MetadataReader reader,
        TypeDefinitionHandle typeHandle,
        string metadataName,
        int parameterCount,
        int genericParameterCount
    )
    {
        foreach (var handle in reader.GetTypeDefinition(typeHandle).GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            if (!string.Equals(reader.GetString(method.Name), metadataName, Ordinal))
                continue;

            if (GetParameterCount(reader, method) != parameterCount)
                continue;

            if (method.GetGenericParameters().Count != genericParameterCount)
                continue;

            return handle;
        }

        return default;
    }

    static PropertyDefinitionHandle FindProperty(MetadataReader reader, TypeDefinitionHandle typeHandle, IPropertySymbol propertySymbol)
    {
        foreach (var handle in reader.GetTypeDefinition(typeHandle).GetProperties())
        {
            var property = reader.GetPropertyDefinition(handle);

            if (!string.Equals(reader.GetString(property.Name), propertySymbol.MetadataName, Ordinal))
                continue;

            var accessors = property.GetAccessors();

            if (!accessors.Getter.IsNil)
                if (GetParameterCount(reader, reader.GetMethodDefinition(accessors.Getter)) == propertySymbol.Parameters.Length)
                    return handle;

            if (!accessors.Setter.IsNil)
                if (GetParameterCount(reader, reader.GetMethodDefinition(accessors.Setter)) == propertySymbol.Parameters.Length + 1)
                    return handle;
        }

        return default;
    }

    static IlMethodInfo BuildMethodInfo(
        MetadataReader reader,
        PEReader peReader,
        MethodDefinitionHandle handle,
        string displayName,
        IlFormatOptions options
    )
    {
        var method = reader.GetMethodDefinition(handle);
        var metadataName = reader.GetString(method.Name);

        if (method.RelativeVirtualAddress is 0)
        {
            return new()
            {
                DisplayName = displayName,
                MetadataName = metadataName,
                Attributes = method.Attributes.ToString(),
                Instructions = ["<no method body>"],
            };
        }

        var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
        var ilBytes = body.GetILBytes() ?? [];
        return new()
        {
            DisplayName = displayName,
            MetadataName = metadataName,
            CodeSize = ilBytes.Length,
            MaxStack = body.MaxStack,
            LocalVariablesInitialized = body.LocalVariablesInitialized,
            Attributes = method.Attributes.ToString(),
            Locals = DecodeLocals(reader, body.LocalSignature, options),
            Instructions = Disassemble(reader, body.GetILReader(), options),
            ExceptionRegions = body.ExceptionRegions
                .AsValueEnumerable()
                .Select(region => FormatExceptionRegion(reader, region, options))
                .ToArray(),
        };
    }

    static string[] Disassemble(MetadataReader reader, BlobReader ilReader, IlFormatOptions options)
    {
        var instructions = new List<string>();
        while (ilReader.RemainingBytes > 0)
        {
            var offset = ilReader.Offset;
            var opCode = ReadOpCode(ref ilReader);
            var operand = ReadOperand(reader, ref ilReader, opCode, options);

            instructions.Add(
                operand.Length is 0
                    ? $"{FormatIlOffset(offset)} {opCode.Name}"
                    : $"{FormatIlOffset(offset)} {opCode.Name} {operand}"
            );
        }

        return [.. instructions];
    }

    static string[] DecodeLocals(MetadataReader reader, StandaloneSignatureHandle handle, IlFormatOptions options)
    {
        if (handle.IsNil)
            return [];

        try
        {
            var provider = new IlSignatureTypeProvider(options);
            var localTypes = reader
                .GetStandaloneSignature(handle)
                .DecodeLocalSignature(provider, IlSignatureContext.Create(options));

            return localTypes
                .AsValueEnumerable()
                .Select(static (type, index) => $"{index} {type}")
                .ToArray();
        }
        catch (BadImageFormatException exception)
        {
            return [$"<invalid local signature: {exception.Message}>"];
        }
    }

    static OpCode ReadOpCode(ref BlobReader reader)
    {
        var firstByte = reader.ReadByte();
        if (firstByte != 0xfe)
            return oneByteOpCodes[firstByte].Name is null
                ? throw new BadImageFormatException($"Unknown IL opcode 0x{firstByte:X2}.")
                : oneByteOpCodes[firstByte];

        var secondByte = reader.ReadByte();
        return twoByteOpCodes[secondByte].Name is null
            ? throw new BadImageFormatException($"Unknown IL opcode 0xfe{secondByte:X2}.")
            : twoByteOpCodes[secondByte];
    }

    static string ReadOperand(MetadataReader metadata, ref BlobReader reader, OpCode opCode, IlFormatOptions options)
        => opCode.OperandType switch
        {
            OperandType.InlineNone          => "",
            OperandType.ShortInlineBrTarget => ReadShortBranchTarget(ref reader),
            OperandType.InlineBrTarget      => ReadBranchTarget(ref reader),
            OperandType.ShortInlineI        => reader.ReadSByte().ToString(CultureInfo.InvariantCulture),
            OperandType.InlineI             => reader.ReadInt32().ToString(CultureInfo.InvariantCulture),
            OperandType.InlineI8            => reader.ReadInt64().ToString(CultureInfo.InvariantCulture),
            OperandType.ShortInlineR        => reader.ReadSingle().ToString("R", CultureInfo.InvariantCulture),
            OperandType.InlineR             => reader.ReadDouble().ToString("R", CultureInfo.InvariantCulture),
            OperandType.ShortInlineVar      => FormatVariableOperand(opCode, reader.ReadByte()),
            OperandType.InlineVar           => FormatVariableOperand(opCode, reader.ReadUInt16()),
            OperandType.InlineSwitch        => ReadSwitchOperand(ref reader),
            OperandType.InlineString        => ResolveUserStringToken(metadata, reader.ReadInt32()),
            OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineTok or OperandType.InlineType
                => ResolveEntityToken(metadata, reader.ReadInt32(), options),
            _ => "",
        };

    static string ReadShortBranchTarget(ref BlobReader reader)
    {
        var delta = reader.ReadSByte();
        return FormatIlOffset(reader.Offset + delta);
    }

    static string ReadBranchTarget(ref BlobReader reader)
    {
        var delta = reader.ReadInt32();
        return FormatIlOffset(reader.Offset + delta);
    }

    static string ReadSwitchOperand(ref BlobReader reader)
    {
        var count = reader.ReadInt32();
        var deltas = new int[count];
        for (int i = 0; i < deltas.Length; i++)
            deltas[i] = reader.ReadInt32();

        var targetBase = reader.Offset;
        return $"({string.Join(", ", deltas.AsValueEnumerable().Select(delta => FormatIlOffset(targetBase + delta)).ToArray())})";
    }

    static string ResolveUserStringToken(MetadataReader reader, int token)
    {
        return MetadataTokens.Handle(token) is { Kind: HandleKind.UserString } handle
            ? Quote(reader.GetUserString((UserStringHandle)handle))
            : "";
    }

    static string ResolveEntityToken(MetadataReader reader, int token, IlFormatOptions options)
    {
        var handle = MetadataTokens.Handle(token);
        return handle.Kind switch
        {
            HandleKind.TypeDefinition      => GetTypeMetadataFullName(reader, (TypeDefinitionHandle)handle, options),
            HandleKind.TypeReference       => GetTypeReferenceName(reader, (TypeReferenceHandle)handle, options),
            HandleKind.TypeSpecification   => GetTypeSpecificationName(reader, (TypeSpecificationHandle)handle, options),
            HandleKind.FieldDefinition     => GetFieldName(reader, (FieldDefinitionHandle)handle, options),
            HandleKind.MethodDefinition    => GetMethodName(reader, (MethodDefinitionHandle)handle, options),
            HandleKind.MemberReference     => GetMemberReferenceName(reader, (MemberReferenceHandle)handle, options),
            HandleKind.MethodSpecification => GetMethodSpecificationName(reader, (MethodSpecificationHandle)handle, options),
            HandleKind.StandaloneSignature => "standalone signature",
            _                              => handle.Kind.ToString(),
        };
    }

    static string FormatVariableOperand(OpCode opCode, int index)
    {
        var name = opCode.Name ?? "";
        if (name.Contains("loc", Ordinal))
            return $"V_{index.ToString(CultureInfo.InvariantCulture)}";

        if (name.Contains("arg", Ordinal))
            return $"arg_{index.ToString(CultureInfo.InvariantCulture)}";

        return index.ToString(CultureInfo.InvariantCulture);
    }

    static string FormatExceptionRegion(MetadataReader reader, ExceptionRegion region, IlFormatOptions options)
    {
        var builder = ZString.CreateStringBuilder();
        try
        {
            builder.Append(region.Kind);
            builder.Append(": try ");
            builder.Append(FormatIlOffset(region.TryOffset));
            builder.Append("..");
            builder.Append(FormatIlOffset(region.TryOffset + region.TryLength));
            builder.Append(", handler ");
            builder.Append(FormatIlOffset(region.HandlerOffset));
            builder.Append("..");
            builder.Append(FormatIlOffset(region.HandlerOffset + region.HandlerLength));

            if (region is { Kind: ExceptionRegionKind.Catch, CatchType.IsNil: false })
            {
                builder.Append(" catch ");
                builder.Append(ResolveEntityHandle(reader, region.CatchType, options));
            }

            if (region.Kind is ExceptionRegionKind.Filter)
            {
                builder.Append(" filter ");
                builder.Append(FormatIlOffset(region.FilterOffset));
            }

            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }

    static string ResolveEntityHandle(MetadataReader reader, EntityHandle handle, IlFormatOptions options)
        => handle.Kind switch
        {
            HandleKind.TypeDefinition      => GetTypeMetadataFullName(reader, (TypeDefinitionHandle)handle, options),
            HandleKind.TypeReference       => GetTypeReferenceName(reader, (TypeReferenceHandle)handle, options),
            HandleKind.TypeSpecification   => GetTypeSpecificationName(reader, (TypeSpecificationHandle)handle, options),
            HandleKind.MethodDefinition    => GetMethodName(reader, (MethodDefinitionHandle)handle, options),
            HandleKind.MemberReference     => GetMemberReferenceName(reader, (MemberReferenceHandle)handle, options),
            HandleKind.MethodSpecification => GetMethodSpecificationName(reader, (MethodSpecificationHandle)handle, options),
            _                              => handle.Kind.ToString(),
        };

    static string GetFieldName(MetadataReader reader, FieldDefinitionHandle handle, IlFormatOptions options)
    {
        var field = reader.GetFieldDefinition(handle);
        return $"{GetTypeMetadataFullName(reader, field.GetDeclaringType(), options)}{GetMemberSeparator(options)}{reader.GetString(field.Name)}";
    }

    static string GetMethodName(MetadataReader reader, MethodDefinitionHandle handle, IlFormatOptions options)
    {
        var method = reader.GetMethodDefinition(handle);
        return $"{GetMethodDefinitionName(reader, handle, includeGenericParameterNames: true, options)}{FormatMethodSignature(DecodeMethodSignature(method, IlSignatureContext.Create(options)))}";
    }

    static string GetMemberReferenceName(MetadataReader reader, MemberReferenceHandle handle, IlFormatOptions options)
    {
        var member = reader.GetMemberReference(handle);
        var name = GetMemberReferenceNameCore(reader, member, options);
        if (member.GetKind() is not MemberReferenceKind.Method)
            return name;

        return $"{name}{FormatMethodSignature(DecodeMethodSignature(member, IlSignatureContext.Create(options)))}";
    }

    static string GetMethodSpecificationName(MetadataReader reader, MethodSpecificationHandle handle, IlFormatOptions options)
    {
        var specification = reader.GetMethodSpecification(handle);
        var arguments = DecodeMethodArguments(specification, options);
        var context = new IlSignatureContext([], arguments, options);
        return specification.Method.Kind switch
        {
            HandleKind.MethodDefinition => FormatMethodSpecification(
                GetMethodDefinitionName(reader, (MethodDefinitionHandle)specification.Method, includeGenericParameterNames: false, options),
                arguments,
                DecodeMethodSignature(reader.GetMethodDefinition((MethodDefinitionHandle)specification.Method), context)
            ),
            HandleKind.MemberReference => FormatMethodSpecification(
                GetMemberReferenceNameCore(reader, reader.GetMemberReference((MemberReferenceHandle)specification.Method), options),
                arguments,
                DecodeMethodSignature(reader.GetMemberReference((MemberReferenceHandle)specification.Method), context)
            ),
            _ => $"{ResolveEntityHandle(reader, specification.Method, options)}{FormatMethodArguments(arguments)}",
        };
    }

    static string GetMethodDefinitionName(
        MetadataReader reader,
        MethodDefinitionHandle handle,
        bool includeGenericParameterNames,
        IlFormatOptions options
    )
    {
        var method = reader.GetMethodDefinition(handle);
        var name = $"{GetTypeMetadataFullName(reader, method.GetDeclaringType(), options)}{GetMemberSeparator(options)}{reader.GetString(method.Name)}";
        return includeGenericParameterNames
            ? $"{name}{FormatGenericParameters(reader, method.GetGenericParameters())}"
            : name;
    }

    static string GetMemberReferenceNameCore(MetadataReader reader, MemberReference member, IlFormatOptions options)
        => $"{ResolveMemberReferenceParent(reader, member.Parent, options)}{GetMemberSeparator(options)}{reader.GetString(member.Name)}";

    static MethodSignature<string>? DecodeMethodSignature(MethodDefinition method, IlSignatureContext? context = null)
    {
        try
        {
            var signatureContext = context ?? IlSignatureContext.Create(IlFormatOptions.Full);
            return method.DecodeSignature(new IlSignatureTypeProvider(signatureContext.Options), signatureContext);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    static MethodSignature<string>? DecodeMethodSignature(MemberReference member, IlSignatureContext? context = null)
    {
        try
        {
            var signatureContext = context ?? IlSignatureContext.Create(IlFormatOptions.Full);
            return member.DecodeMethodSignature(new IlSignatureTypeProvider(signatureContext.Options), signatureContext);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    static ImmutableArray<string> DecodeMethodArguments(MethodSpecification specification, IlFormatOptions options)
    {
        try
        {
            return specification.DecodeSignature(new IlSignatureTypeProvider(options), IlSignatureContext.Create(options));
        }
        catch (BadImageFormatException)
        {
            return [];
        }
    }

    static string FormatMethodSignature(MethodSignature<string>? signature)
        => signature is null
            ? ""
            : $"({string.Join(", ", signature.Value.ParameterTypes)})";

    static string FormatMethodArguments(ImmutableArray<string> arguments)
        => arguments.Length is 0
            ? ""
            : $"<{string.Join(", ", arguments)}>";

    static string FormatMethodSpecification(
        string name,
        ImmutableArray<string> arguments,
        MethodSignature<string>? signature
    )
        => $"{name}{FormatMethodArguments(arguments)}{FormatMethodSignature(signature)}";

    static string FormatGenericParameters(MetadataReader reader, GenericParameterHandleCollection handles)
    {
        if (handles.Count is 0)
            return "";

        var names = handles
            .AsValueEnumerable()
            .Select(handle => reader.GetString(reader.GetGenericParameter(handle).Name))
            .ToArray();

        return $"<{string.Join(", ", names)}>";
    }

    static string ResolveMemberReferenceParent(MetadataReader reader, EntityHandle parent, IlFormatOptions options)
        => parent.Kind switch
        {
            HandleKind.TypeDefinition    => GetTypeMetadataFullName(reader, (TypeDefinitionHandle)parent, options),
            HandleKind.TypeReference     => GetTypeReferenceName(reader, (TypeReferenceHandle)parent, options),
            HandleKind.TypeSpecification => GetTypeSpecificationName(reader, (TypeSpecificationHandle)parent, options),
            HandleKind.MethodDefinition  => GetMethodName(reader, (MethodDefinitionHandle)parent, options),
            HandleKind.ModuleReference   => "module",
            _                            => parent.Kind.ToString(),
        };

    static string GetTypeReferenceName(MetadataReader reader, TypeReferenceHandle handle, IlFormatOptions options)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        var @namespace = reader.GetString(type.Namespace);
        return GetTypeDisplayName(@namespace, name, options);
    }

    static string GetTypeSpecificationName(MetadataReader reader, TypeSpecificationHandle handle)
        => GetTypeSpecificationName(reader, handle, IlFormatOptions.Full);

    static string GetTypeSpecificationName(MetadataReader reader, TypeSpecificationHandle handle, IlFormatOptions options)
        => GetTypeSpecificationName(reader, handle, IlSignatureContext.Create(options));

    static string GetTypeSpecificationName(MetadataReader reader, TypeSpecificationHandle handle, IlSignatureContext context)
    {
        try
        {
            return reader
                .GetTypeSpecification(handle)
                .DecodeSignature(new IlSignatureTypeProvider(context.Options), context);
        }
        catch (BadImageFormatException)
        {
            return "typespec";
        }
    }

    static string GetTypeMetadataFullName(MetadataReader reader, TypeDefinitionHandle handle)
        => GetTypeMetadataFullName(reader, handle, IlFormatOptions.Full);

    static string GetTypeMetadataFullName(MetadataReader reader, TypeDefinitionHandle handle, IlFormatOptions options)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = StripGenericArity(reader.GetString(type.Name));
        if (type.IsNested)
            return $"{GetTypeMetadataFullName(reader, type.GetDeclaringType(), options)}.{name}";

        var @namespace = reader.GetString(type.Namespace);
        return GetTypeDisplayName(@namespace, name, options);
    }

    static string GetTypeMetadataFullName(INamedTypeSymbol type)
    {
        if (type.ContainingType is not null)
            return $"{GetTypeMetadataFullName(type.ContainingType)}.{StripGenericArity(type.MetadataName)}";

        var @namespace = type.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
            ? containingNamespace.ToDisplayString()
            : "";

        return GetTypeDisplayName(@namespace, type.MetadataName, IlFormatOptions.Full);
    }

    static string GetTypeDisplayName(string @namespace, string name, IlFormatOptions options)
    {
        var simpleName = StripGenericArity(name);
        return @namespace is "System"
            ? simpleName switch
            {
                "Boolean" => "bool",
                "Byte"    => "byte",
                "SByte"   => "sbyte",
                "Char"    => "char",
                "Decimal" => "decimal",
                "Double"  => "double",
                "Single"  => "float",
                "Int16"   => "short",
                "UInt16"  => "ushort",
                "Int32"   => "int",
                "UInt32"  => "uint",
                "Int64"   => "long",
                "UInt64"  => "ulong",
                "Object"  => "object",
                "String"  => "string",
                "Void"    => "void",
                _         => $"{@namespace}.{simpleName}",
            }
            : options.Compact || @namespace.Length is 0 ? simpleName : $"{@namespace}.{simpleName}";
    }

    static string GetMemberSeparator(IlFormatOptions options)
        => options.Compact ? "." : "::";

    static string StripGenericArity(string name)
    {
        var index = name.IndexOf('`', Ordinal);
        return index < 0
            ? name
            : name[..index];
    }

    static int GetParameterCount(MetadataReader reader, MethodDefinition method)
        => method.GetParameters().AsValueEnumerable().Count(x => reader.GetParameter(x).SequenceNumber > 0);

    static string GetMethodMetadataName(IMethodSymbol method)
        => method.MethodKind switch
        {
            MethodKind.Constructor       => ".ctor",
            MethodKind.StaticConstructor => ".cctor",
            _                            => method.MetadataName,
        };

    static string FormatIlOffset(int offset) => offset.ToString("X4", CultureInfo.InvariantCulture);

    static string Quote(string value) => $"\"{value.Replace("\\", "\\\\", Ordinal).Replace("\"", "\\\"", Ordinal)}\"";

    readonly record struct IlFormatOptions(bool Compact)
    {
        public static IlFormatOptions Full { get; } = new(false);
    }

    readonly record struct IlSignatureContext(
        ImmutableArray<string> TypeArguments,
        ImmutableArray<string> MethodArguments,
        IlFormatOptions Options
    )
    {
        public static IlSignatureContext Create(IlFormatOptions options)
            => new([], [], options);
    }

    sealed class IlSignatureTypeProvider : ISignatureTypeProvider<string, IlSignatureContext>
    {
        readonly IlFormatOptions options;

        public IlSignatureTypeProvider(IlFormatOptions options)
        {
            this.options = options;
        }

        public string GetArrayType(string elementType, ArrayShape shape)
            => $"{elementType}[{new string(',', Math.Max(shape.Rank - 1, 0))}]";

        public string GetByReferenceType(string elementType)
            => $"{elementType}&";

        public string GetFunctionPointerType(MethodSignature<string> signature)
            => $"method {signature.ReturnType} *({string.Join(", ", signature.ParameterTypes)})";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => $"{genericType}<{string.Join(", ", typeArguments)}>";

        public string GetGenericMethodParameter(IlSignatureContext genericContext, int index)
            => index < genericContext.MethodArguments.Length
                ? genericContext.MethodArguments[index]
                : $"!!{index.ToString(CultureInfo.InvariantCulture)}";

        public string GetGenericTypeParameter(IlSignatureContext genericContext, int index)
            => index < genericContext.TypeArguments.Length
                ? genericContext.TypeArguments[index]
                : $"!{index.ToString(CultureInfo.InvariantCulture)}";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
            => $"{unmodifiedType}{(isRequired ? " modreq(" : " modopt(")}{modifier})";

        public string GetPinnedType(string elementType)
            => $"{elementType} pinned";

        public string GetPointerType(string elementType)
            => $"{elementType}*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
            => typeCode switch
            {
                PrimitiveTypeCode.Void           => "void",
                PrimitiveTypeCode.Boolean        => "bool",
                PrimitiveTypeCode.Char           => "char",
                PrimitiveTypeCode.SByte          => "sbyte",
                PrimitiveTypeCode.Byte           => "byte",
                PrimitiveTypeCode.Int16          => "short",
                PrimitiveTypeCode.UInt16         => "ushort",
                PrimitiveTypeCode.Int32          => "int",
                PrimitiveTypeCode.UInt32         => "uint",
                PrimitiveTypeCode.Int64          => "long",
                PrimitiveTypeCode.UInt64         => "ulong",
                PrimitiveTypeCode.Single         => "float",
                PrimitiveTypeCode.Double         => "double",
                PrimitiveTypeCode.String         => "string",
                PrimitiveTypeCode.TypedReference => "typedref",
                PrimitiveTypeCode.IntPtr         => "native int",
                PrimitiveTypeCode.UIntPtr        => "native uint",
                PrimitiveTypeCode.Object         => "object",
                _                                => typeCode.ToString().ToLowerInvariant(),
            };

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => GetTypeMetadataFullName(reader, handle, options);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => GetTypeReferenceName(reader, handle, options);

        public string GetTypeFromSpecification(
            MetadataReader reader,
            IlSignatureContext genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind
        )
            => GetTypeSpecificationName(reader, handle, genericContext);
    }
}
