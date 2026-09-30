#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
#:package Avalonia@12.*
#:project ../src/FrameFlow.Avalonia.Windows/FrameFlow.Avalonia.Windows.csproj

// Checks that the Avalonia packages, compiled against Avalonia 11, bind against Avalonia 12.
//
// This app references Avalonia 12 and the Avalonia projects. The projects still compile
// against 11, and the app runs them on 12, as an app on 12 that references the packages
// does. Avalonia keeps binary compatibility only within a major version, and a DLL built
// against 11 that uses a member 12 removed or reshaped still loads: it fails with
// MissingMethodException when the method using it first runs. Rebuilding against 12 can't
// see that, because the rebuilt DLL is not the one that ships.
//
// For FrameFlow.Avalonia and FrameFlow.Avalonia.Windows, the app loads every type the
// assembly declares and binds every type, member and generic method instantiation it
// references, through the runtime's own binder. A reference that names a type parameter binds
// only with type arguments, so it is bound in each place the assembly uses it: the method
// bodies that name it, and the base types, interfaces, explicit implementations and
// constraints of the types that do, each with that method's or type's own type parameters.
//
//   dotnet run scripts/avalonia-binary-check.cs
//   dotnet run scripts/avalonia-binary-check.cs -c Release    # reuses a Release build, as CI does
//
// Exits non-zero, naming each reference, when any fails to bind.

using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Avalonia;

var runtime = typeof(AvaloniaObject).Assembly.GetName().Version!;
Console.WriteLine($"Avalonia at run time: {runtime}");
if (runtime.Major != 12)
{
    Console.Error.WriteLine($"avalonia binary check: the app resolved Avalonia {runtime}, not 12");
    return 1;
}

int failures = 0;
foreach (var name in (string[])["FrameFlow.Avalonia", "FrameFlow.Avalonia.Windows"])
    failures += Check(Assembly.Load(name));

Console.WriteLine(failures == 0
    ? $"avalonia binary check: every reference binds against Avalonia {runtime}"
    : $"avalonia binary check: {failures} reference(s) failed to bind");
return failures == 0 ? 0 : 1;

static int Check(Assembly assembly)
{
    string name = assembly.GetName().Name!;
    var built = assembly.GetReferencedAssemblies().Single(r => r.Name == "Avalonia.Base").Version!;
    if (built.Major != 11)
    {
        Console.Error.WriteLine($"{name}: compiled against Avalonia {built}, not 11, so this is not the DLL that ships");
        return 1;
    }

    var failed = new SortedDictionary<int, string>();
    var bound = new HashSet<int>();

    // References that name a type parameter, and each context one has been bound in. A token
    // is shared by every use with the same signature, and the type parameters it names carry
    // the constraints of whichever type or method it is used in, so each context is bound.
    var generic = new HashSet<int>();
    var bindings = new HashSet<(int Token, Type[] TypeArguments, Type[] MethodArguments)>();

    // A declared type fails to load when its base type or an interface it implements broke.
    try
    {
        assembly.GetTypes();
    }
    catch (ReflectionTypeLoadException ex)
    {
        foreach (var error in ex.LoaderExceptions)
            Console.Error.WriteLine($"  type load: {error!.Message}");
        return ex.LoaderExceptions.Length;
    }

    var module = assembly.ManifestModule;
    using var pe = new PEReader(File.OpenRead(assembly.Location));
    var metadata = pe.GetMetadataReader();

    void Bind(int token, Type[]? typeArguments, Type[]? methodArguments)
    {
        try
        {
            module.ResolveMember(token, typeArguments, methodArguments);
            bound.Add(token);

            // A member of a generic instantiation binds its parent type, in the same context.
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind == HandleKind.MemberReference
                && metadata.GetMemberReference((MemberReferenceHandle)handle).Parent is { Kind: HandleKind.TypeSpecification } parent)
            {
                BindInContext(parent, typeArguments ?? [], methodArguments ?? []);
            }
        }
        catch (ArgumentException ex) when (ex.InnerException is BadImageFormatException && typeArguments is null && methodArguments is null)
        {
            generic.Add(token);
        }
        catch (Exception ex)
        {
            // A missing field arrives as ArgumentOutOfRangeException: ResolveField swallows the
            // MissingFieldException and retries the token as a literal.
            failed.TryAdd(token, $"{Describe(metadata, token)}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    foreach (var handle in metadata.TypeReferences)
        Bind(MetadataTokens.GetToken(handle), null, null);
    foreach (var handle in metadata.MemberReferences)
        Bind(MetadataTokens.GetToken(handle), null, null);
    for (int row = 1; row <= metadata.GetTableRowCount(TableIndex.TypeSpec); row++)
        Bind(MetadataTokens.GetToken(MetadataTokens.TypeSpecificationHandle(row)), null, null);
    for (int row = 1; row <= metadata.GetTableRowCount(TableIndex.MethodSpec); row++)
        Bind(MetadataTokens.GetToken(MetadataTokens.MethodSpecificationHandle(row)), null, null);

    void BindInContext(EntityHandle handle, Type[] typeArguments, Type[] methodArguments)
    {
        int token = MetadataTokens.GetToken(handle);
        if (generic.Contains(token) && bindings.Add((token, typeArguments, methodArguments)))
            Bind(token, typeArguments, methodArguments);
    }

    // In the context of the type that extends, implements or constrains with them, and of each
    // method body that uses them.
    foreach (var typeHandle in metadata.TypeDefinitions)
    {
        var definition = metadata.GetTypeDefinition(typeHandle);
        // Row 1 is <Module>, which reflection doesn't return as a type.
        Type[] typeArguments = MetadataTokens.GetRowNumber(typeHandle) == 1
            ? []
            : module.ResolveType(MetadataTokens.GetToken(typeHandle)).GetGenericArguments();
        if (!definition.BaseType.IsNil)
            BindInContext(definition.BaseType, typeArguments, []);
        foreach (var handle in definition.GetInterfaceImplementations())
            BindInContext(metadata.GetInterfaceImplementation(handle).Interface, typeArguments, []);
        foreach (var handle in definition.GetMethodImplementations())
            BindInContext(metadata.GetMethodImplementation(handle).MethodDeclaration, typeArguments, []);
        foreach (var parameter in definition.GetGenericParameters())
        {
            foreach (var constraint in metadata.GetGenericParameter(parameter).GetConstraints())
                BindInContext(metadata.GetGenericParameterConstraint(constraint).Type, typeArguments, []);
        }

        foreach (var methodHandle in definition.GetMethods())
        {
            var method = module.ResolveMethod(MetadataTokens.GetToken(methodHandle))!;
            Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : [];
            foreach (var parameter in metadata.GetMethodDefinition(methodHandle).GetGenericParameters())
            {
                foreach (var constraint in metadata.GetGenericParameter(parameter).GetConstraints())
                    BindInContext(metadata.GetGenericParameterConstraint(constraint).Type, typeArguments, methodArguments);
            }

            foreach (int token in Tokens(method))
                BindInContext(MetadataTokens.EntityHandle(token), typeArguments, methodArguments);
        }
    }

    var unbound = generic.Where(t => !bound.Contains(t) && !failed.ContainsKey(t)).ToList();
    Console.WriteLine($"{name} (compiled against Avalonia {built}): {bound.Count(t => !failed.ContainsKey(t))} references bound, "
        + $"{bindings.Count} generic bindings in context, {failed.Count + unbound.Count} not bound");
    foreach (var failure in failed.Values)
        Console.Error.WriteLine($"  {failure}");
    foreach (int token in unbound)
        Console.Error.WriteLine($"  {Describe(metadata, token)}: generic, and nothing in the assembly gives it a context to bind in");
    return failed.Count + unbound.Count;
}

static string Describe(MetadataReader metadata, int token)
{
    var handle = MetadataTokens.EntityHandle(token);
    switch (handle.Kind)
    {
        case HandleKind.TypeReference:
            var type = metadata.GetTypeReference((TypeReferenceHandle)handle);
            return $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}";
        case HandleKind.MemberReference:
            var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
            string parent = member.Parent.Kind == HandleKind.TypeReference
                ? Describe(metadata, MetadataTokens.GetToken(member.Parent))
                : $"0x{MetadataTokens.GetToken(member.Parent):X8}";
            return $"{parent}::{metadata.GetString(member.Name)}";
        case HandleKind.MethodSpecification:
            var method = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            return $"{Describe(metadata, MetadataTokens.GetToken(method))}<...>";
        default:
            return $"0x{token:X8}";
    }
}

// The metadata tokens a method's IL names: the operands of call, newobj, ldfld, ldtoken and the like.
static IEnumerable<int> Tokens(MethodBase method)
{
    byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
    if (il is null)
        yield break;

    for (int i = 0; i < il.Length;)
    {
        OpCode op = il[i] == 0xFE ? OpCodeTable.TwoByte[il[i + 1]] : OpCodeTable.OneByte[il[i]];
        i += op.Size;
        switch (op.OperandType)
        {
            case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok:
                yield return BitConverter.ToInt32(il, i);
                i += 4;
                break;
            case OperandType.InlineSwitch:
                i += 4 + 4 * BitConverter.ToInt32(il, i);
                break;
            case OperandType.InlineI8 or OperandType.InlineR:
                i += 8;
                break;
            case OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineSig or OperandType.InlineString or OperandType.ShortInlineR:
                i += 4;
                break;
            case OperandType.InlineVar:
                i += 2;
                break;
            case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                i += 1;
                break;
        }
    }
}

static class OpCodeTable
{
    public static readonly OpCode[] OneByte = new OpCode[0x100];
    public static readonly OpCode[] TwoByte = new OpCode[0x100];

    static OpCodeTable()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)field.GetValue(null)!;
            if (op.Size == 1)
                OneByte[(byte)op.Value] = op;
            else
                TwoByte[(byte)op.Value] = op;
        }
    }
}
