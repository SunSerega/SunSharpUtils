using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using SunSharpUtils.DataStash.Analyzer;
using SunSharpUtils.Ext.Linq;

//TODO Split the RPC out to a separate library?
// - 2 libs cause 1 for attribute, 1 for analyzer
// - Need another common lib for gen utils (SymbolExt)
// --- Or it could be in just common ext lib, check size increase

//TODO Fix error ids once they are stable

namespace SunSharpUtils.DataStash.Generators;

[Generator]
[SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1041:Compiler extensions should be implemented in assemblies targeting netstandard2.0", Justification = "I can ensure it only runs on .Net10")]
internal class CodeGenerator : IIncrementalGenerator
{

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {

        context.RegisterPostInitializationOutput(context =>
        {
            const String to_copy_res_prefix = "SunSharpUtils.DataStash.ToCopy.";
            foreach (var resource_name in typeof(CodeGenerator).Assembly.GetManifestResourceNames())
            {
                if (!resource_name.StartsWith(to_copy_res_prefix))
                    continue;
                var file_name = resource_name[to_copy_res_prefix.Length..];
                if (Path.GetExtension(file_name) != ".cs")
                    throw new InvalidOperationException($"Expected only .cs files but found: {file_name}");
                file_name = Path.ChangeExtension(file_name, ".g.cs");
                var stream = typeof(CodeGenerator).Assembly.GetManifestResourceStream(resource_name)
                    ?? throw new InvalidOperationException($"Resource reported but cannot be read: {resource_name}");
                var reader = new StreamReader(stream);
                var resource_content = reader.ReadToEnd();
                context.AddSource(file_name, SourceText.From(resource_content, GenConstants.Encoding));
            }
        });

        #region Rpc

        var rpc_api_methods = context.SyntaxProvider.ForAttributeWithMetadataName(
            typeof(RpcApiAttribute).FullName!,
            (node, _) => true,
            (context, _) => (IMethodSymbol)context.TargetSymbol
        ).Collect();
        context.RegisterSourceOutput(rpc_api_methods, (context, symbols) =>
        {
            foreach (var g in symbols.GroupBy(s => s.ContainingType, SymbolEqualityComparer.Default))
            {
                var containing_type = (INamedTypeSymbol?)g.Key ?? throw new InvalidOperationException("Containing type is null");
                var namespace_name = containing_type.ContainingNamespace.IsGlobalNamespace ? null : containing_type.ContainingNamespace.ToDisplayString();

                if (!containing_type.ValidateAsPartialClass(context, "DS_RPC001", "DS_RPC002"))
                    continue;

                var methods = g.Select(m => new RpcApi.Method
                {
                    Name = m.Name,
                    Accessibility = m.DeclaredAccessibility.ConvertToGenStr(),
                    ReturnType = m.ReturnType.SpecialType is SpecialType.System_Void ? null : m.ReturnType.ToDisplayString(),
                    Parameters = m.Parameters.ToArray(p => new RpcApi.Method.Parameter
                    {
                        Name = p.Name,
                        Type = p.Type.ToDisplayString()
                    })
                }).ToArray();

                var source_code = CodeSourceGenerator.Gen(gen =>
                {
                    gen += $"using System;";
                    gen += $"using System.IO;";
                    gen += $"using System.Threading;";
                    gen += $"using System.Net.Sockets;";
                    gen += $"";
                    gen += $"using SunSharpUtils;";
                    gen += $"using SunSharpUtils.DataStash;";
                    gen += $"using SunSharpUtils.Ext.Bin;";
                    gen += $"using SunSharpUtils.Ext.UniversalBin;";
                    gen += $"";

                    if (namespace_name is { })
                    {
                        gen += $"namespace {namespace_name};";
                        gen += $"";
                    }

                    gen += "file enum EClientCommand";
                    gen.AddBlock(gen =>
                    {
                        gen += "Invalid = 0,";
                        foreach (var method in methods)
                            gen += $"{method.Name},";
                    });
                    gen += $"";

                    gen += $"{containing_type.DeclaredAccessibility.ConvertToGenStr()} static partial class {containing_type.Name}";
                    gen.AddBlock(gen =>
                    {
                        gen += $"public static readonly {GenConstants.ClientConnectorClassName} ClientConnector = new(\"{containing_type.Name}\");";
                        gen += $"";

                        gen += $"public readonly struct ProcessClientConfig";
                        gen.AddBlock(gen =>
                        {
                            gen += $"public required Socket Socket {{ get; init; }}";
                            gen += $"public required CancellationToken CancelToken {{ get; init; }}";
                            foreach (var method in methods)
                            {
                                gen += $"";
                                gen += $"public delegate {method.ReturnType ?? "void"} {method.Name}Handler({method.Parameters.Select(p => $"{p.Type} {p.Name}").Append("CancellationToken cancel_token").JoinToString(", ")});";
                                gen += $"public required {method.Name}Handler On{method.Name} {{ get; init; }}";
                            }
                        });
                        gen += $"public static void ProcessClient(ProcessClientConfig config)";
                        gen.AddBlock(gen =>
                        {
                            gen += $"var stream = new NetworkStream(config.Socket);";
                            gen += $"var bw = new BinaryWriter(stream);";
                            gen += $"var br = new BinaryReader(stream);";
                            gen += $"try";
                            gen.AddBlock(gen =>
                            {
                                gen += $"var client_cmd = br.ReadEnum<EClientCommand>();";
                                gen += $"switch (client_cmd)";
                                gen.AddBlock(gen =>
                                {
                                    foreach (var method in methods)
                                    {
                                        gen += $"case EClientCommand.{method.Name}:";
                                        gen.AddBlock(gen =>
                                        {
                                            foreach (var parameter in method.Parameters)
                                                gen += $"var {parameter.Name} = br.ReadData<{parameter.Type}>();";
                                            gen.AddLine(gen =>
                                            {
                                                if (method.ReturnType is not null)
                                                    gen *= $"var result = ";
                                                gen *= "config.On";
                                                gen *= method.Name;
                                                gen *= ".Invoke(";
                                                foreach (var parameter in method.Parameters)
                                                {
                                                    gen *= parameter.Name;
                                                    gen *= ", ";
                                                }
                                                gen *= "config.CancelToken);";
                                            });
                                            if (method.ReturnType is not null)
                                                gen += $"bw.WriteData(result);";
                                            gen += $"break;";
                                        });
                                    }
                                    gen += $"default:";
                                    gen.AddTab(gen =>
                                    {
                                        gen += $"throw new NotImplementedException($\"Unknown client command: {{client_cmd}}\");";
                                    });
                                });
                                gen += $"bw.WriteEnum({GenConstants.ServerCommandEnumName}.{nameof(RpcApiUtils.EServerCommand.Success)});";
                            });
                            gen += $"catch (Exception ex)";
                            gen.AddBlock(gen =>
                            {
                                gen += $"Err.HandleDuring(() =>";
                                gen.AddBlock(gen =>
                                {
                                    gen += $"if (!config.Socket.Connected)";
                                    gen.AddTab(gen =>
                                    {
                                        gen += $"return;";
                                    });
                                    gen += $"bw.WriteEnum({GenConstants.ServerCommandEnumName}.{nameof(RpcApiUtils.EServerCommand.Error)});";
                                    gen += $"bw.Write(ex.Message);";
                                }, "{", "});");
                                gen += $"throw;";
                            });
                        });
                        gen += $"";

                        foreach (var method in methods)
                        {
                            gen.AddLine(gen =>
                            {
                                gen *= method.Accessibility;
                                gen *= " static partial ";
                                gen *= method.ReturnType ?? "void";
                                gen *= " ";
                                gen *= method.Name;
                                gen *= "(";
                                gen.AddSeq(method.Parameters, (gen, param) =>
                                {
                                    gen *= param.Type;
                                    gen *= " ";
                                    gen *= param.Name;
                                }, ", ");
                                gen *= ") => ClientConnector.Connect(conn =>";
                            });
                            gen.AddBlock(gen =>
                            {
                                gen += $"conn.Writer.WriteEnum(EClientCommand.{method.Name});";
                                foreach (var parameter in method.Parameters)
                                    gen += $"conn.Writer.WriteData({parameter.Name});";
                                if (method.ReturnType is { } ret_type)
                                {
                                    gen += $"conn.Writer.Flush();";
                                    gen += $"return conn.Reader.ReadData<{ret_type}>();";
                                }
                            }, "{", "});");
                            gen += $"";
                        }
                    });
                });

                context.AddSource($"{containing_type.Name}_{nameof(RpcApi)}.g.cs", SourceText.From(source_code, GenConstants.Encoding));
            }
        });

        #endregion

        #region DataStash

        var data_stash_types = context.SyntaxProvider.ForAttributeWithMetadataName(
            typeof(AutoDataStashAttribute).FullName!,
            (node, _) => true,
            (context, _) => (type: (INamedTypeSymbol)context.TargetSymbol, attrib: context.Attributes.Single(a=>a.AttributeClass!.Name == nameof(AutoDataStashAttribute)))
        );
        context.RegisterSourceOutput(data_stash_types, (context, gen_item) =>
        {
            var (data_stash_type, attrib) = gen_item;
            if (data_stash_type.BaseType is not { } data_stash_base_type || data_stash_base_type.Name != nameof(DataStash<>) || data_stash_base_type.TypeArguments.Length != 1)
            {
                data_stash_type.ReportOnAllDeclaringSyntax(
                    context,
                    id: "DS001",
                    title: "Invalid base type for DataStash",
                    messageFormat: "DataStash type '{0}' needs to inherit from DataStash<>",
                    DiagnosticSeverity.Error,
                    args: [data_stash_type.Name]
                );
                return;
            }
            if (!data_stash_type.ValidateAsPartialClass(context, "DS002", "DS003"))
                return;
            if (data_stash_type.ContainingType is not null)
            {
                data_stash_type.ReportOnAllDeclaringSyntax(
                    context,
                    id: "DS004",
                    title: "Unsupported type declaration for data stash",
                    messageFormat: "DataStash type '{0}' isn't expected to be nested",
                    DiagnosticSeverity.Error,
                    args: [data_stash_type.Name]
                );
                return;
            }

            var namespace_name = data_stash_type.ContainingNamespace.IsGlobalNamespace ? null : data_stash_type.ContainingNamespace.ToDisplayString();
            var typed_content_type = data_stash_base_type.TypeArguments.Single();
            if (!SymbolEqualityComparer.Default.Equals(typed_content_type.ContainingType, data_stash_type))
            {
                data_stash_type.ReportOnAllDeclaringSyntax(
                    context,
                    id: "DS005",
                    title: "Invalid declaration of DataStash typed content type",
                    messageFormat: "DataStash content type '{0}' should be declared nested in its DataStash type '{1}'",
                    DiagnosticSeverity.Error,
                    args: [typed_content_type.ToDisplayString(), data_stash_type.ToDisplayString()]
                );
                return;
            }

            var file_blocks = new Dictionary<String, (INamedTypeSymbol network_data_type, INamedTypeSymbol file_data_type, INamedTypeSymbol model_type)>();
            var typed_model_parents = new Dictionary<String, INamedTypeSymbol?>();
            {
                var core_implemented = false;
                foreach (var impl_type in typed_content_type.Interfaces)
                {
                    switch (impl_type.Name)
                    {
                        case nameof(DataStash<>.ITypedContent<,>):
                            if (core_implemented)
                            {
                                typed_content_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS006",
                                    title: "Multiple core interface implementations for typed content",
                                    messageFormat: "Typed content type '{0}' implements core interface '{1}' multiple times",
                                    DiagnosticSeverity.Error,
                                    args: [typed_content_type.Name, impl_type.Name]
                                );
                            }
                            if (impl_type.TypeArguments.Length != 2)
                            {
                                typed_content_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS007",
                                    title: "Unexpected number of type arguments",
                                    messageFormat: "Typed content type '{0}' implements interface '{1}' with unexpected number of type arguments ({2} instead of {3})",
                                    DiagnosticSeverity.Error,
                                    args: [typed_content_type.Name, impl_type.Name, impl_type.TypeArguments.Length, 2]
                                );
                                return;
                            }
                            core_implemented = true;
                            break;
                        case nameof(DataStash<>.ITypedContentWithRootBlock<,,>):
                        {
                            if (impl_type.TypeArguments.Length != 3)
                            {
                                typed_content_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS007",
                                    title: "Unexpected number of type arguments",
                                    messageFormat: "Typed content type '{0}' implements interface '{1}' with unexpected number of type arguments ({2} instead of {3})",
                                    DiagnosticSeverity.Error,
                                    args: [typed_content_type.Name, impl_type.Name, impl_type.TypeArguments.Length, 3]
                                );
                                continue;
                            }
                            var network_data_type = (INamedTypeSymbol)impl_type.TypeArguments[0];
                            var file_data_type = (INamedTypeSymbol)impl_type.TypeArguments[1];
                            var model_type = (INamedTypeSymbol)impl_type.TypeArguments[2];
                            file_blocks.Add(model_type.Name, (network_data_type, file_data_type, model_type));
                            typed_model_parents.Add(model_type.Name, null);
                            break;
                        }
                        case nameof(DataStash<>.ITypedContentWithChildBlock<,,,>):
                        {
                            if (impl_type.TypeArguments.Length != 4)
                            {
                                typed_content_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS007",
                                    title: "Unexpected number of type arguments",
                                    messageFormat: "Typed content type '{0}' implements interface '{1}' with unexpected number of type arguments ({2} instead of {3})",
                                    DiagnosticSeverity.Error,
                                    args: [typed_content_type.Name, impl_type.Name, impl_type.TypeArguments.Length, 4]
                                );
                                continue;
                            }
                            var network_data_type = (INamedTypeSymbol)impl_type.TypeArguments[0];
                            var file_data_type = (INamedTypeSymbol)impl_type.TypeArguments[1];
                            var parent_model_type = (INamedTypeSymbol)impl_type.TypeArguments[2];
                            var model_type = (INamedTypeSymbol)impl_type.TypeArguments[3];
                            file_blocks.Add(model_type.Name, (network_data_type, file_data_type, model_type));
                            typed_model_parents.Add(model_type.Name, parent_model_type);
                            break;
                        }
                        default:
                            typed_content_type.ReportOnAllDeclaringSyntax(
                                context,
                                id: "DS008",
                                title: "Unexpected interface implemented by typed content",
                                messageFormat: "Typed content type '{0}' implements unexpected interface '{1}'",
                                DiagnosticSeverity.Warning,
                                args: [typed_content_type.Name, impl_type.Name]
                            );
                            break;
                    }

                }
            }
            foreach (var kv in file_blocks)
            {
                var model_type = kv.Value.model_type;
                if (!model_type.ValidateAsPartialClass(context, "DS006", "DS007"))
                    return;
                if (!SymbolEqualityComparer.Default.Equals(model_type.ContainingType, typed_content_type))
                {
                    data_stash_type.ReportOnAllDeclaringSyntax(
                        context,
                        id: "DS005",
                        title: "Invalid declaration of DataStash typed content type",
                        messageFormat: "DataStash content model type '{0}' should be declared nested in its DataStash typed content type '{1}'",
                        DiagnosticSeverity.Error,
                        args: [model_type.ToDisplayString(), typed_content_type.ToDisplayString()]
                    );
                    return;
                }
            }
            var all_parent_model_names = typed_model_parents.Values.OfType<INamedTypeSymbol>().Select(t => t.Name).ToHashSet();

            var source_code = CodeSourceGenerator.Gen(gen =>
            {
                gen += $"using System;";
                gen += $"using System.IO;";
                gen += $"";

                if (namespace_name is { })
                {
                    gen += $"namespace {namespace_name};";
                    gen += $"";
                }

                gen += $"{data_stash_type.DeclaredAccessibility.ConvertToGenStr()} partial class {data_stash_type.Name}";
                gen.AddBlock(gen =>
                {
                    gen += $"";

                    #region TypedContent

                    gen += $"public sealed partial class {typed_content_type.Name}";
                    gen.AddBlock(gen =>
                    {
                        gen += $"";

                        gen += $"public void ApplyBlock(BinaryReader br, DateTime record_time, BlockLocation location)";
                        gen.AddBlock(gen =>
                        {
                            //TODO
                            gen += $"throw new NotImplementedException();";
                        });
                        gen += $"";

                        gen += $"public void LogSealHeldByBlocks(BlockLocation[] block_locations)";
                        gen.AddBlock(gen =>
                        {
                            //TODO
                            gen += $"throw new NotImplementedException();";
                        });
                        gen += $"";

                        gen += $"public void Resave(Stream stream)";
                        gen.AddBlock(gen =>
                        {
                            //TODO
                            gen += $"throw new NotImplementedException();";
                        });
                        gen += $"";

                        foreach (var kv in file_blocks)
                        {
                            var model_type = kv.Value.model_type;
                            gen.AddLine(gen =>
                            {
                                gen *= "public sealed partial ";
                                if (model_type.IsRecord)
                                    gen *= "record ";
                                gen *= "class ";
                                gen *= model_type.Name;
                            });
                            gen.AddBlock(gen =>
                            {
                                gen += $"public required DateTime RecordTime {{ get; init; }}";
                            });
                            gen += $"";
                        }

                    });
                    gen += $"";

                    #endregion

                    #region ResaveContext

                    String ResaveContextClassName(String? container_model_name)
                    {
                        var res = "ResaveContext";
                        if (container_model_name is not null)
                            res += $"_{container_model_name}";
                        return res;
                    }

                    foreach (var container_model_name in all_parent_model_names.Prepend(null))
                    {
                        gen += $"private sealed class {ResaveContextClassName(container_model_name)}";
                        gen.AddBlock(gen =>
                        {
                            gen += $"";

                            foreach (var (model_name, parent_model_type) in typed_model_parents)
                            {
                                Boolean ShouldGenerate()
                                {
                                    if (parent_model_type?.Name == container_model_name)
                                        return true;
                                    if (container_model_name is null && parent_model_type!.NullableAnnotation.HasFlag(NullableAnnotation.Annotated))
                                        return true;
                                    return false;
                                }
                                if (!ShouldGenerate())
                                    continue;

                                gen.AddLine(gen =>
                                {
                                    var (network_data_type, file_data_type, model_type) = file_blocks[model_name];
                                    gen *= "public void AddBlock(";
                                    gen *= model_type.ToDisplayString();
                                    gen *= " model, ";
                                    gen *= file_data_type.ToDisplayString();
                                    gen *= " file_data";
                                    if (all_parent_model_names.Contains(model_name))
                                    {
                                        gen *= ", Action<";
                                        gen *= ResaveContextClassName(model_name);
                                        gen *= "> resave_children";
                                    }
                                    gen *= ")";
                                });
                                gen.AddBlock(gen =>
                                {
                                    //TODO
                                    gen += $"throw new NotImplementedException();";
                                });
                                gen += $"";

                            }

                        });
                        gen += $"";
                    }

                    #endregion

                });

            });
            context.AddSource($"{data_stash_type.Name}.g.cs", SourceText.From(source_code, GenConstants.Encoding));
        });

        #endregion

    }

    private static class RpcApi
    {

        public readonly struct Method
        {
            public required String Name { get; init; }
            public required String Accessibility { get; init; }

            public required String? ReturnType { get; init; }
            public required Parameter[] Parameters { get; init; }

            public readonly struct Parameter
            {
                public required String Name { get; init; }
                public required String Type { get; init; }
            }
        }

    }

}

file static class SymbolExt
{

    public static String ConvertToGenStr(this Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        _ => throw new NotImplementedException($"Unexpected accessibility: {accessibility}")
    };

    public static Boolean ValidateAsPartialClass(this INamedTypeSymbol symbol, SourceProductionContext context, String err_id_not_class, String err_id_not_partial)
    {
        var is_valid = true;
        foreach (var syntax in symbol.DeclaringSyntaxReferences.Select(r => (TypeDeclarationSyntax)r.GetSyntax()))
        {
            var is_class = syntax is ClassDeclarationSyntax || syntax is RecordDeclarationSyntax rec && rec.ClassOrStructKeyword.IsKind(SyntaxKind.ClassKeyword);
            if (!is_class)
            {
                context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
                    id: err_id_not_class,
                    title: "Type must be a class",
                    messageFormat: "Type '{0}' needs to be a class",
                    category: "CodeGenerator",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true
                ), syntax.GetLocation(), symbol.Name));
                is_valid = false;
                continue;
            }
            if (!syntax.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
                    id: err_id_not_partial,
                    title: "Class must be partial",
                    messageFormat: "Class '{0}' must be partial",
                    category: "CodeGenerator",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true
                ), syntax.GetLocation(), symbol.Name));
                is_valid = false;
                continue;
            }
        }
        return is_valid;
    }

    public static void ReportOnAllDeclaringSyntax(this ISymbol symbol, SourceProductionContext context, String id, String title, String messageFormat, DiagnosticSeverity severity, Func<SyntaxNode, Object?[]?>? make_args = null)
    {
        foreach (var syntax_ref in symbol.DeclaringSyntaxReferences)
        {
            var syntax = syntax_ref.GetSyntax();
            context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
                id, title, messageFormat,
                category: "CodeGenerator",
                severity,
                isEnabledByDefault: true
            ), syntax.GetLocation(), make_args?.Invoke(syntax)));
        }
    }
    public static void ReportOnAllDeclaringSyntax(this ISymbol symbol, SourceProductionContext context, String id, String title, String messageFormat, DiagnosticSeverity severity, Object?[]? args) =>
        symbol.ReportOnAllDeclaringSyntax(context, id, title, messageFormat, severity, _ => args);

}
