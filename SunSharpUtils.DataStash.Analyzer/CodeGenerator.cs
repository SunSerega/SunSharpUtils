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
using SunSharpUtils.UniversalBin;

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

                var methods = g.Select(m =>
                {
                    if (m.Parameters.Length != 0 && m.Parameters[^1] is { } last_param && last_param.Type.TypeKind is TypeKind.Delegate)
                    {
                        var del_type = (INamedTypeSymbol)last_param.Type;
                        var del_invoke_method = del_type.DelegateInvokeMethod ?? throw new InvalidOperationException($"Delegate type {del_type.ToDisplayString()} has no invoke method");
                        if (!del_invoke_method.ReturnsVoid)
                        {
                            m.ReportOnAllDeclaringSyntax(
                                context,
                                id: "DS_RPC003",
                                title: "Invalid delegate return type for RPC method",
                                messageFormat: "RPC method '{0}' has a delegate parameter '{1}' with non-void return type '{2}'",
                                DiagnosticSeverity.Error,
                                args: [m.Name, last_param.Name, del_invoke_method.ReturnType.ToDisplayString()]
                            );
                        }
                        var del_params = del_invoke_method.Parameters;

                        var stream_type_name = "StreamedItemType";
                        if (
                            del_params is [.., var last_del_param] &&
                            last_del_param.Type is INamedTypeSymbol last_del_param_type &&
                            last_del_param_type.Name == nameof(RpcEnumerable<>) &&
                            last_del_param_type.TypeArguments.Length == 1
                            )
                        {
                            stream_type_name = last_del_param_type.TypeArguments[0].ToDisplayString();
                        }
                        else
                        {
                            m.ReportOnAllDeclaringSyntax(
                                context,
                                id: "DS_RPC004",
                                title: "Invalid delegate parameter for RPC method",
                                messageFormat: "RPC method '{0}' with streamed parameter '{1}' must have a last parameter of type RpcEnumerable<T>",
                                DiagnosticSeverity.Error,
                                args: [m.Name, last_param.Name]
                            );
                        }

                        if (!m.ReturnsVoid)
                        {
                            m.ReportOnAllDeclaringSyntax(
                                context,
                                id: "DS_RPC005",
                                title: "Invalid return type for RPC method",
                                messageFormat: "RPC method '{0}' with streamed parameter '{1}' must have a void return type",
                                DiagnosticSeverity.Error,
                                args: [m.Name, last_param.Name]
                            );
                        }

                        return new RpcApi.MethodStreamed
                        {
                            Name = m.Name,
                            Accessibility = m.DeclaredAccessibility.ConvertToGenStr(),
                            NonStreamedParameters = m.Parameters[..^1].ToArray(p => new RpcApi.Method.NameAndType
                            {
                                Name = p.Name,
                                Type = p.Type.ToDisplayString()
                            }),
                            StreamCallbackParameter = new()
                            {
                                Name = last_param.Name,
                                Type = last_param.Type.ToDisplayString()
                            },
                            ReturnedValues = del_params[..^1].ToArray(p => new RpcApi.Method.NameAndType
                            {
                                Name = p.Name,
                                Type = p.Type.ToDisplayString()
                            }),
                            StreamedItemType = stream_type_name,
                        };
                    }

                    return (RpcApi.Method)new RpcApi.MethodOneOff
                    {
                        Name = m.Name,
                        Accessibility = m.DeclaredAccessibility.ConvertToGenStr(),
                        ReturnType = m.ReturnType.SpecialType is SpecialType.System_Void ? null : m.ReturnType.ToDisplayString(),
                        Parameters = m.Parameters.ToArray(p => new RpcApi.Method.NameAndType
                        {
                            Name = p.Name,
                            Type = p.Type.ToDisplayString()
                        })
                    };
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
                    gen += $"using SunSharpUtils.UniversalBin;";
                    gen += $"";

                    if (namespace_name is { })
                    {
                        gen += $"namespace {namespace_name};";
                        gen += $"";
                    }

                    gen += $"#nullable enable";
                    gen += $"";

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
                                gen.AddLine(gen =>
                                {
                                    gen *= "public delegate ";
                                    switch (method)
                                    {
                                        case RpcApi.MethodOneOff one_off_method:
                                            gen *= one_off_method.ReturnType ?? "void";
                                            gen *= " ";
                                            gen *= method.Name;
                                            gen *= "Handler(";
                                            foreach (var param in one_off_method.Parameters)
                                            {
                                                gen *= param.Type;
                                                gen *= " ";
                                                gen *= param.Name;
                                                gen *= ", ";
                                            }
                                            break;
                                        case RpcApi.MethodStreamed streamed_method:
                                            if (streamed_method.ReturnedValues.Length != 0)
                                            {
                                                gen *= "(";
                                                foreach (var param in streamed_method.ReturnedValues)
                                                {
                                                    gen *= param.Type;
                                                    gen *= ", ";
                                                }
                                            }
                                            gen *= nameof(RpcEnumerableSource<>);
                                            gen *= "<";
                                            gen *= streamed_method.StreamedItemType;
                                            gen *= ">";
                                            if (streamed_method.ReturnedValues.Length != 0)
                                                gen *= ")";
                                            gen *= " ";
                                            gen *= method.Name;
                                            gen *= "Handler(";
                                            foreach (var param in streamed_method.NonStreamedParameters)
                                            {
                                                gen *= param.Type;
                                                gen *= " ";
                                                gen *= param.Name;
                                                gen *= ", ";
                                            }
                                            break;
                                        default:
                                            throw new NotImplementedException($"Expected method type: {method.GetType().Name}");
                                    }
                                    gen *= "CancellationToken cancel_token);";
                                });
                                gen += $"public required {method.Name}Handler On{method.Name} {{ get; init; }}";
                            }
                        });
                        var need_keep_socket_open_ret_arg = methods.OfType<RpcApi.MethodStreamed>().Any();
                        gen += $"public readonly struct ProcessClientResult";
                        gen.AddBlock(gen =>
                        {
                            if (need_keep_socket_open_ret_arg)
                                gen += $"public required Boolean KeepSocketOpen {{ get; init; }}";
                        });
                        gen += $"public static ProcessClientResult ProcessClient(ProcessClientConfig config)";
                        gen.AddBlock(gen =>
                        {
                            gen += $"var streamed_method = new NetworkStream(config.Socket);";
                            gen += $"var bw = new BinaryWriter(streamed_method);";
                            gen += $"var br = new BinaryReader(streamed_method);";
                            gen += $"try";
                            gen.AddBlock(gen =>
                            {
                                gen += $"var client_cmd = br.ReadEnum<EClientCommand>();";
                                if (need_keep_socket_open_ret_arg)
                                    gen += $"Boolean keep_socket_open;";
                                gen += $"switch (client_cmd)";
                                gen.AddBlock(gen =>
                                {
                                    foreach (var method in methods)
                                    {
                                        gen += $"case EClientCommand.{method.Name}:";
                                        gen.AddBlock(gen =>
                                        {
                                            switch (method)
                                            {
                                                case RpcApi.MethodOneOff one_off_method:
                                                    foreach (var parameter in one_off_method.Parameters)
                                                        gen += $"var {parameter.Name} = br.ReadData<{parameter.Type}>();";
                                                    gen.AddLine(gen =>
                                                    {
                                                        if (one_off_method.ReturnType is not null)
                                                            gen *= $"var result = ";
                                                        gen *= "config.On";
                                                        gen *= one_off_method.Name;
                                                        gen *= ".Invoke(";
                                                        foreach (var parameter in one_off_method.Parameters)
                                                        {
                                                            gen *= parameter.Name;
                                                            gen *= ", ";
                                                        }
                                                        gen *= "config.CancelToken);";
                                                    });
                                                    if (one_off_method.ReturnType is not null)
                                                        gen += $"bw.WriteData(result);";
                                                    gen += $"bw.WriteEnum({GenConstants.ServerCommandEnumName}.{nameof(RpcApiUtils.EServerCommand.Success)});";
                                                    break;
                                                case RpcApi.MethodStreamed streamed_method:
                                                    foreach (var parameter in streamed_method.NonStreamedParameters)
                                                        gen += $"var {parameter.Name} = br.ReadData<{parameter.Type}>();";
                                                    gen.AddLine(gen =>
                                                    {
                                                        gen *= $"var ";
                                                        if (streamed_method.ReturnedValues.Length != 0)
                                                        {
                                                            gen *= "(";
                                                            gen.AddSeq(streamed_method.ReturnedValues, (gen, param) =>
                                                            {
                                                                gen *= param.Name;
                                                            }, ", ");
                                                            gen *= ", ";
                                                        }
                                                        gen *= "enumerable_source";
                                                        if (streamed_method.ReturnedValues.Length != 0)
                                                            gen *= ")";
                                                        gen *= $" = config.On";
                                                        gen *= streamed_method.Name;
                                                        gen *= ".Invoke(";
                                                        foreach (var parameter in streamed_method.NonStreamedParameters)
                                                        {
                                                            gen *= parameter.Name;
                                                            gen *= ", ";
                                                        }
                                                        gen *= "config.CancelToken);";
                                                    });
                                                    foreach (var param in streamed_method.ReturnedValues)
                                                        gen += $"bw.WriteData({param.Name});";
                                                    gen += $"enumerable_source.Subscribe(config.Socket);";
                                                    break;
                                                default:
                                                    throw new NotImplementedException($"Expected method type: {method.GetType().Name}");
                                            }
                                            if (need_keep_socket_open_ret_arg)
                                                gen += $"keep_socket_open = {(method is RpcApi.MethodStreamed).ToString().ToLower()};";
                                            gen += $"break;";
                                        });
                                    }
                                    gen += $"default:";
                                    gen.AddTab(gen =>
                                    {
                                        gen += $"throw new NotImplementedException($\"Unknown client command: {{client_cmd}}\");";
                                    });
                                });
                                gen += $"return new()";
                                gen.AddBlock(gen =>
                                {
                                    if (need_keep_socket_open_ret_arg)
                                        gen += $"KeepSocketOpen = keep_socket_open,";
                                }, "{", "};");
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
                            switch (method)
                            {
                                case RpcApi.MethodOneOff one_off_method:
                                {
                                    gen.AddLine(gen =>
                                    {
                                        gen *= one_off_method.Accessibility;
                                        gen *= " static partial ";
                                        gen *= one_off_method.ReturnType ?? "void";
                                        gen *= " ";
                                        gen *= one_off_method.Name;
                                        gen *= "(";
                                        gen.AddSeq(one_off_method.Parameters, (gen, param) =>
                                        {
                                            gen *= param.Type;
                                            gen *= " ";
                                            gen *= param.Name;
                                        }, ", ");
                                        gen *= ") => ClientConnector.Connect(conn =>";
                                    });
                                    gen.AddBlock(gen =>
                                    {
                                        gen += $"conn.Writer.WriteEnum(EClientCommand.{one_off_method.Name});";
                                        foreach (var parameter in one_off_method.Parameters)
                                            gen += $"conn.Writer.WriteData({parameter.Name});";
                                        if (one_off_method.ReturnType is { } ret_type)
                                        {
                                            gen += $"conn.Writer.Flush();";
                                            gen += $"return conn.Reader.ReadData<{ret_type}>();";
                                        }
                                    }, "{", "});");
                                    break;
                                }
                                case RpcApi.MethodStreamed streamed_method:
                                {
                                    gen.AddLine(gen =>
                                    {
                                        gen *= streamed_method.Accessibility;
                                        gen *= " static partial void";
                                        gen *= " ";
                                        gen *= streamed_method.Name;
                                        gen *= "(";
                                        gen.AddSeq(streamed_method.NonStreamedParameters.Append(streamed_method.StreamCallbackParameter), (gen, param) =>
                                        {
                                            gen *= param.Type;
                                            gen *= " ";
                                            gen *= param.Name;
                                        }, ", ");
                                        gen *= ")";
                                    });
                                    gen.AddBlock(gen =>
                                    {
                                        gen += $"var thr = new Thread(ThreadProc)";
                                        gen.AddBlock(gen =>
                                        {
                                            gen.AddLine(gen =>
                                            {
                                                gen *= "Name = $\"";
                                                gen *= containing_type.Name;
                                                gen *= ".";
                                                gen *= streamed_method.Name;
                                                gen *= "(";
                                                gen.AddSeq(streamed_method.NonStreamedParameters, (gen, param) =>
                                                {
                                                    gen *= "{";
                                                    gen *= param.Name;
                                                    gen *= "}";
                                                }, ", ");
                                                gen *= ") processing thread\",";
                                            });
                                            gen += $"IsBackground = false,";
                                        }, "{", "};");
                                        gen += $"thr.Start();";
                                        gen += $"void ThreadProc() => ClientConnector.Connect(conn =>";
                                        gen.AddBlock(gen =>
                                        {
                                            gen += $"conn.Writer.WriteEnum(EClientCommand.{streamed_method.Name});";
                                            foreach (var parameter in streamed_method.NonStreamedParameters)
                                                gen += $"conn.Writer.WriteData({parameter.Name});";
                                            gen += $"conn.Writer.Flush();";
                                            foreach (var param in streamed_method.ReturnedValues)
                                                gen += $"var {param.Name} = conn.Reader.ReadData<{param.Type}>();";
                                            gen += $"var enumerable = new {nameof(RpcEnumerable<>)}<{streamed_method.StreamedItemType}>(conn.Socket);";
                                            gen.AddLine(gen =>
                                            {
                                                gen *= streamed_method.StreamCallbackParameter.Name;
                                                gen *= ".Invoke(";
                                                foreach (var param in streamed_method.ReturnedValues)
                                                {
                                                    gen *= param.Name;
                                                    gen *= ", ";
                                                }
                                                gen *= "enumerable);";
                                            });
                                        }, "{", "});");
                                    });
                                    break;
                                }
                                default:
                                    throw new NotImplementedException($"Expected method type: {method.GetType().Name}");
                            }
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
            (context, _) => (type: (INamedTypeSymbol)context.TargetSymbol, attrib: context.Attributes.Single(a => a.AttributeClass!.Name == nameof(AutoDataStashAttribute)))
        );
        context.RegisterSourceOutput(data_stash_types, (context, gen_item) =>
        {
            var (data_stash_type, attrib) = gen_item;
            if (data_stash_type.BaseType is not { } data_stash_base_type || data_stash_base_type.Name != nameof(DataStash<,>) || data_stash_base_type.TypeArguments.Length != 2)
            {
                data_stash_type.ReportOnAllDeclaringSyntax(
                    context,
                    id: "DS001",
                    title: "Invalid base type for DataStash",
                    messageFormat: "DataStash type '{0}' needs to inherit from DataStash<,>",
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
            var typed_content_type = data_stash_base_type.TypeArguments.Skip(1).Single();
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
            var closable_typed_models = new Dictionary<String, (INamedTypeSymbol key_type, INamedTypeSymbol model_type)>();
            {
                var core_implemented = false;
                foreach (var impl_type in typed_content_type.Interfaces)
                {
                    switch (impl_type.Name)
                    {
                        case nameof(DataStash<,>.ITypedContent<,>):
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
                        case nameof(DataStash<,>.ITypedContentWithRootBlock<,,>):
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
                        case nameof(DataStash<,>.ITypedContentWithChildBlock<,,,>):
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
                        case nameof(DataStash<,>.ITypedContentWithCloseableBlock<,>):
                        {
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
                                continue;
                            }
                            var key_type = (INamedTypeSymbol)impl_type.TypeArguments[0];
                            var model_type = (INamedTypeSymbol)impl_type.TypeArguments[1];
                            closable_typed_models.Add(model_type.Name, (key_type, model_type));
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
            foreach (var (network_data_type, file_data_type, model_type) in file_blocks.Values)
            {
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
                if (!file_data_type.GetAttributes().Any(attrib => attrib.AttributeClass?.Name == nameof(VersionedDataAttribute)))
                {
                    file_data_type.ReportOnAllDeclaringSyntax(
                        context,
                        id: "DS009",
                        title: "Missing [VersionedData] attribute on file data type",
                        messageFormat: "File data type '{0}' should have a [VersionedData] attribute",
                        DiagnosticSeverity.Warning,
                        args: [file_data_type.ToDisplayString()]
                    );
                }
            }
            var all_parent_model_names = typed_model_parents.Values
                .OfType<INamedTypeSymbol>()
                .Select(t => t.Name)
                .ToHashSet();

            var source_code = CodeSourceGenerator.Gen(gen =>
            {
                gen += $"using System;";
                gen += $"using System.IO;";
                gen += $"using System.Linq;";
                gen += $"using System.Diagnostics.CodeAnalysis;";
                gen += $"";
                gen += $"using SunSharpUtils;";
                gen += $"using SunSharpUtils.Ext.Bin;";
                gen += $"using SunSharpUtils.Ext.Linq;";
                gen += $"";

                if (namespace_name is { })
                {
                    gen += $"namespace {namespace_name};";
                    gen += $"";
                }

                gen += $"#nullable enable";
                gen += $"";

                gen += $"file enum EBlockKind : Byte";
                gen.AddBlock(gen =>
                {
                    gen += $"Invalid = 0,";
                    foreach (var kv in file_blocks)
                    {
                        var model_type = kv.Value.model_type;
                        gen += $"{model_type.Name},";
                    }
                });
                gen += $"";

                gen += $"{data_stash_type.DeclaredAccessibility.ConvertToGenStr()} partial class {data_stash_type.Name}";
                gen.AddBlock(gen =>
                {
                    gen += $"";

                    #region Network methods

                    foreach (var (network_data_type, file_data_type, model_type) in file_blocks.Values)
                    {
                        gen += $"public {model_type.ToDisplayString()} Add{model_type.Name}({network_data_type.ToDisplayString()} network_data)";
                        gen.AddBlock(gen =>
                        {
                            if (typed_model_parents[model_type.Name] is { } parent_model_type)
                            {
                                var parent_can_be_null = parent_model_type.NullableAnnotation.HasFlag(NullableAnnotation.Annotated);
                                gen += $"var parent = this.PendingCollectOne<{parent_model_type.ToDisplayString()}>($\"Looking for parent from network data {{network_data}}\", (content, [MaybeNullWhen(false)] out result) => content.TryGetParent(network_data, out result){(parent_can_be_null ? ", on_not_found: () => null" : null)});";
                                if (parent_can_be_null)
                                {
                                    gen += $"if (parent is {{ }})";
                                    gen.AddBlock(gen =>
                                    {
                                        gen += $"var location = parent.CommonInfo.Location;";
                                        GenInvokeAddNewBlock(gen, has_parent: true, can_have_parent: true);
                                    });
                                    gen += $"else";
                                    gen.AddBlock(gen =>
                                    {
                                        gen += $"return this.UseNewWriteLocation(location =>";
                                        gen.AddBlock(gen => GenInvokeAddNewBlock(gen, has_parent: false, can_have_parent: true), "{", "});");
                                    });
                                }
                                else
                                {
                                    gen += $"var location = parent.CommonInfo.Location;";
                                    GenInvokeAddNewBlock(gen, has_parent: true, can_have_parent: true);
                                }
                            }
                            else
                            {
                                gen += $"return this.UseNewWriteLocation(location =>";
                                gen.AddBlock(gen => GenInvokeAddNewBlock(gen, has_parent: false, can_have_parent: false), "{", "});");
                            }

                            void GenInvokeAddNewBlock(CodeSourceGenerator gen, Boolean has_parent, Boolean can_have_parent)
                            {
                                gen += $"return location.AddNewBlock(";
                                gen.AddTab(gen =>
                                {
                                    var hold_open = closable_typed_models.ContainsKey(model_type.Name);
                                    gen.AddLine(gen =>
                                    {
                                        gen *= "hold_open: ";
                                        gen *= hold_open.ToString().ToLower();
                                        gen *= ", EBlockKind.";
                                        gen *= model_type.Name;
                                        gen *= ", add_parent_ref: ";
                                        gen *= has_parent.ToString().ToLower();
                                        gen *= ", ";
                                        gen *= typed_content_type.Name;
                                        gen *= ".ParseNetworkPacket(this, network_data),";
                                    });
                                    gen.AddLine(gen =>
                                    {
                                        gen *= "(content, common_info, file_data) => content.ReadBlock(common_info, ";
                                        if (can_have_parent)
                                        {
                                            gen *= "parent";
                                            if (!has_parent)
                                                gen *= ": null";
                                            gen *= ", ";
                                        }
                                        gen *= "file_data, is_new: true)";
                                    });
                                });
                                gen += $");";
                            }
                        });
                        gen += $"";
                    }

                    foreach (var (key_type, model_type) in closable_typed_models.Values)
                    {
                        gen += $"public void Close{model_type.Name}({key_type.ToDisplayString()} key)";
                        gen.AddBlock(gen =>
                        {
                            gen += $"var model = this.PendingCollectOne<{model_type.ToDisplayString()}>($\"Searching model {model_type.Name}[{{key}}] for closing\", (content, [MaybeNullWhen(false)] out result) => content.TryGetModelByKey(key, out result));";
                            gen += $"if (!model.IsOpen)";
                            gen.AddBlock(gen =>
                            {
                                gen += $"Prompt.Notify($\"{{this}}: Model {{model}} is already closed\");";
                                gen += $"return;";
                            });
                            gen += $"model.CommonInfo.Location.CloseBlock();";
                            gen += $"model.IsOpen = false;";
                        });
                        gen += $"";
                    }

                    foreach (var (key_type, model_type) in closable_typed_models.Values)
                    {
                        gen += $"public {key_type.ToDisplayString()}[] SyncAllOpen{model_type.Name}({key_type.ToDisplayString()}[] producer_side_keys)";
                        gen.AddBlock(gen =>
                        {
                            gen.AddLine(gen =>
                            {
                                gen *= "var locally_open = this.PendingCollectAndOrganize<";
                                gen *= key_type.ToDisplayString();
                                gen *= ", ";
                                gen *= model_type.ToDisplayString();
                                gen *= ">((content, [MaybeNullWhen(false)] out result) => content.CollectAllOpenModels(out result), ";
                                gen *= typed_content_type.ToDisplayString();
                                gen *= ".GetModelKey);";
                            });
                            gen += $"";
                            gen += $"// Don't force close recently created models";
                            gen += $"var max_force_close_record_time = DateTime.UtcNow.AddMinutes(-10);";
                            gen += $"foreach (var key in locally_open.Keys.Except(producer_side_keys).ToArray())";
                            gen.AddBlock(gen =>
                            {
                                gen += $"var (file_id, model) = locally_open[key];";
                                gen += $"if (model.CommonInfo.RecordTimeUtc > max_force_close_record_time)";
                                gen.AddTab(gen =>
                                {
                                    gen += $"continue;";
                                });
                                gen += $"// Doesn't matter if the model could not be closed, this method is only eventually consistent";
                                gen += $"file_id.TryUsePendingContent(this, content =>";
                                gen.AddBlock(gen =>
                                {
                                    gen += $"if (content.TryGetModelByKey(key, out {model_type.ToDisplayString()}? model) && model.IsOpen)";
                                    gen.AddBlock(gen =>
                                    {
                                        gen += $"model.CommonInfo.Location.CloseBlock();";
                                        gen += $"model.IsOpen = false;";
                                        gen += $"Prompt.Notify($\"{{this}}: Force closed {{model}}, because producer does not recognize it\");";
                                    });
                                }, "{", "});");
                                gen += $"locally_open.Remove(key);";
                            });
                            gen += $"";
                            gen += $"return locally_open.Keys.ToArray();";
                        });
                        gen += $"";
                    }

                    #endregion

                    #region TypedContent

                    gen += $"public sealed partial class {typed_content_type.Name}";
                    gen.AddBlock(gen =>
                    {
                        gen += $"";

                        gen += $"public void ApplyBlock(CommonTypedModelInfo common_info, ReadContext context)";
                        gen.AddBlock(gen =>
                        {
                            gen += $"var command = context.ReadCommand<EBlockKind>();";
                            gen += $"switch (command)";
                            gen.AddBlock(gen =>
                            {
                                foreach (var (network_data_type, file_data_type, model_type) in file_blocks.Values)
                                {
                                    gen += $"case EBlockKind.{model_type.Name}:";
                                    gen.AddBlock(gen =>
                                    {
                                        if (typed_model_parents[model_type.Name] is { } parent_model_type)
                                        {
                                            gen += $"var parent_location = context.ReadParentLocation();";
                                            gen += $"if (!this.TryGetModelByLocation(parent_location, out {parent_model_type.Name}? parent))";
                                            gen.AddTab(gen =>
                                            {
                                                gen += $"throw new InvalidOperationException($\"{{context.Description}}: Parent {parent_model_type.Name} not found at {{parent_location}} when reading child {model_type.Name} at {{common_info.Location}}\");";
                                            });
                                        }
                                        gen += $"var file_data = context.ReadFileData<{file_data_type.ToDisplayString()}>();";
                                        gen.AddLine(gen =>
                                        {
                                            gen *= "this.ReadBlock(common_info, ";
                                            if (typed_model_parents[model_type.Name] is { })
                                                gen *= "parent, ";
                                            gen *= "file_data, is_new: false);";
                                        });
                                        gen += $"break;";
                                    });
                                }
                                gen += $"default:";
                                gen.AddTab(gen =>
                                {
                                    gen += $"throw new InvalidDataException($\"{{context.Description}}: Invalid block kind: {{command}}\");";
                                });
                            });
                        });
                        gen += $"";

                        gen += $"public void CloseModel(BlockLocation location)";
                        gen.AddBlock(gen =>
                        {
                            foreach (var model_name in closable_typed_models.Keys)
                            {
                                gen += $"if (this.TryGetModelByLocation(location, out {model_name}? model_{model_name}))";
                                gen.AddBlock(gen =>
                                {
                                    gen += $"model_{model_name}.IsOpen = false;";
                                    gen += $"return;";
                                });
                            }
                            gen += $"throw new InvalidOperationException($\"No closable model found at {{location}}\");";
                        });
                        gen += $"";

                        gen += $"public void LogSealHeldByBlocks(String file_group_description, BlockLocation[] block_locations)";
                        gen.AddBlock(gen =>
                        {
                            gen += $"var models = block_locations.ToArray(GetModelByLocation);";
                            gen += $"Prompt.Notify($\"{{models.Length}} models are preventing sealing of {{file_group_description}}: {{models.JoinToString(\"; \")}}\");";
                            gen += $"";
                            gen += $"Object GetModelByLocation(BlockLocation location)";
                            gen.AddBlock(gen =>
                            {
                                foreach (var model_name in closable_typed_models.Keys)
                                {
                                    gen += $"if (this.TryGetModelByLocation(location, out {model_name}? model_{model_name}))";
                                    gen.AddTab(gen =>
                                    {
                                        gen += $"return model_{model_name};";
                                    });
                                }
                                gen += $"throw new InvalidOperationException($\"No closable model found at {{location}}\");";
                            });
                        });
                        gen += $"";

                        gen += $"public void Resave(ResaveContext context) => this.Resave(new TypedResaveContext(context, DateTime.MinValue));";
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
                                gen += $"public required CommonTypedModelInfo CommonInfo {{ get; init; }}";
                                if (typed_model_parents[model_type.Name] is { } parent_model_type)
                                    gen += $"public required {parent_model_type.ToDisplayString()} Parent {{ get; init; }}";
                                if (closable_typed_models.ContainsKey(model_type.Name))
                                    gen += $"public Boolean IsOpen {{ get; set; }} = true;";
                            });
                            gen += $"";
                        }

                    });
                    gen += $"";

                    #endregion

                    #region ResaveContext

                    String ResaveContextClassName(String? container_model_name)
                    {
                        var res = "TypedResaveContext";
                        if (container_model_name is not null)
                            res += $"_{container_model_name}";
                        return res;
                    }

                    foreach (var container_model_name in all_parent_model_names.Prepend(null))
                    {
                        gen += $"public sealed class {ResaveContextClassName(container_model_name)}(ResaveContext context, DateTime last_record_time_utc)";
                        gen.AddBlock(gen =>
                        {
                            gen += $"private readonly ResaveContext context = context;";
                            gen += $"public DateTime last_record_time_utc = last_record_time_utc;";
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
                                    gen *= " model";
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
                                    gen += $"if (model.CommonInfo.RecordTimeUtc < this.last_record_time_utc)";
                                    gen.AddTab(gen =>
                                    {
                                        gen += $"throw new InvalidOperationException($\"{data_stash_type.Name}.{ResaveContextClassName(container_model_name)}: Cannot write {model_name} block with record time {{model.CommonInfo.RecordTimeUtc}} before last written record time {{this.last_record_time_utc}}\");";
                                    });
                                    gen += $"this.last_record_time_utc = model.CommonInfo.RecordTimeUtc;";
                                    gen.AddLine(gen =>
                                    {
                                        gen *= "this.context.WriteBlock(model.CommonInfo, EBlockKind.";
                                        gen *= model_name;
                                        gen *= ", ";
                                        if (parent_model_type is { })
                                            gen *= "model.Parent?.CommonInfo.Location, ";
                                        else
                                            gen *= "parent_location: null, ";
                                        gen *= "model.ConvertToFileData());";
                                    });
                                    if (all_parent_model_names.Contains(model_name))
                                        gen += $"resave_children.Invoke(new {ResaveContextClassName(model_name)}(this.context, this.last_record_time_utc));";
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

        public abstract class Method
        {
            public required String Name { get; init; }
            public required String Accessibility { get; init; }

            public readonly struct NameAndType
            {
                public required String Name { get; init; }
                public required String Type { get; init; }
            }

        }

        public sealed class MethodOneOff : Method
        {
            public required String? ReturnType { get; init; }
            public required NameAndType[] Parameters { get; init; }
        }

        public sealed class MethodStreamed : Method
        {
            public required NameAndType[] NonStreamedParameters { get; init; }
            public required NameAndType StreamCallbackParameter { get; init; }
            public required NameAndType[] ReturnedValues { get; init; }
            public required String StreamedItemType { get; init; }
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
