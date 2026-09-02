using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using SunSharpUtils.DataStash.Analyzer;
using SunSharpUtils.Ext.Linq;

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

                foreach (var syntax in containing_type.DeclaringSyntaxReferences.Select(r => r.GetSyntax()))
                {
                    if (syntax is not ClassDeclarationSyntax class_declaration)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
                            id: "DS001",
                            title: "Invalid type declaration for rpc",
                            messageFormat: "Containing type '{0}' needs to be a class",
                            category: "CodeGenerator",
                            DiagnosticSeverity.Error,
                            isEnabledByDefault: true
                        ), syntax.GetLocation(), containing_type.Name));
                        continue;
                    }
                    if (!class_declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
                            id: "DS002",
                            title: "Class must be partial",
                            messageFormat: "Containing class '{0}' must be declared as partial",
                            category: "CodeGenerator",
                            DiagnosticSeverity.Error,
                            isEnabledByDefault: true
                        ), class_declaration.GetLocation(), containing_type.Name));
                        continue;
                    }
                }

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

}
