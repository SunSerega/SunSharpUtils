using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
            try
            {
                foreach (var g in symbols.GroupBy(s => s.ContainingType, SymbolEqualityComparer.Default))
                {
                    var containing_type = (INamedTypeSymbol?)g.Key ?? throw new InvalidOperationException("Containing type is null");
                    var namespace_name = containing_type.ContainingNamespace.IsGlobalNamespace ? null : containing_type.ContainingNamespace.ToDisplayString();

                    if (!containing_type.ValidateAsPartialClass(context, "DS_RPC001", "DS_RPC002"))
                        continue;

                    var methods = g.Select(m =>
                    {
                        if (m.Parameters.Length >= 2 && m.Parameters is [.., var stream_callback_param, var read_cancel_token_param] && stream_callback_param.Type.TypeKind is TypeKind.Delegate)
                        {
                            var del_type = (INamedTypeSymbol)stream_callback_param.Type;
                            var del_invoke_method = del_type.DelegateInvokeMethod ?? throw new InvalidOperationException($"Delegate type {del_type.ToDisplayString()} has no invoke method");
                            if (!del_invoke_method.ReturnsVoid && del_invoke_method.ReturnType.ToDisplayString() != typeof(Task).FullName)
                            {
                                m.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS_RPC003",
                                    title: "Invalid delegate return type for RPC method",
                                    messageFormat: "RPC method '{0}' with streamed parameter '{1}' must return either void or Task, not '{2}'",
                                    DiagnosticSeverity.Error,
                                    args: [m.Name, stream_callback_param.Name, del_invoke_method.ReturnType.ToDisplayString()]
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
                                    messageFormat: "RPC method '{0}' with streamed parameter '{1}' must have a second to last parameter of type RpcEnumerable<T>",
                                    DiagnosticSeverity.Error,
                                    args: [m.Name, stream_callback_param.Name]
                                );
                            }

                            if (read_cancel_token_param.Type.ToDisplayString() != typeof(CancellationToken).FullName)
                            {
                                m.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS_RPC005",
                                    title: "Invalid read cancel token parameter for RPC method",
                                    messageFormat: "RPC method '{0}' with streamed parameter '{1}' must have a last parameter of type CancellationToken",
                                    DiagnosticSeverity.Error,
                                    args: [m.Name, stream_callback_param.Name]
                                );
                            }

                            if (!m.ReturnsVoid)
                            {
                                m.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS_RPC006",
                                    title: "Invalid return type for RPC method",
                                    messageFormat: "RPC method '{0}' with streamed parameter '{1}' must return void, not '{2}'",
                                    DiagnosticSeverity.Error,
                                    args: [m.Name, stream_callback_param.Name, m.ReturnType.ToDisplayString()]
                                );
                            }

                            return new RpcApi.MethodStreamed
                            {
                                Name = m.Name,
                                Accessibility = m.DeclaredAccessibility.ConvertToGenStr(),
                                NonStreamedParameters = m.Parameters[..^2].ToArray(p => new RpcApi.NameAndType
                                {
                                    Name = p.Name,
                                    Type = p.Type.ToDisplayString()
                                }),
                                StreamCallbackParameter = new()
                                {
                                    Name = stream_callback_param.Name,
                                    Type = stream_callback_param.Type.ToDisplayString()
                                },
                                ReadCancelTokenParameterName = read_cancel_token_param.Name,
                                ReturnedValues = del_params[..^1].ToArray(p => new RpcApi.NameAndType
                                {
                                    Name = p.Name,
                                    Type = p.Type.ToDisplayString()
                                }),
                                StreamedItemType = stream_type_name,
                                CallbackReturnsTask = !del_invoke_method.ReturnsVoid,
                            };
                        }

                        return (RpcApi.Method)new RpcApi.MethodOneOff
                        {
                            Name = m.Name,
                            Accessibility = m.DeclaredAccessibility.ConvertToGenStr(),
                            ReturnType = m.ReturnType.SpecialType is SpecialType.System_Void ? null : m.ReturnType.ToDisplayString(),
                            Parameters = m.Parameters.ToArray(p => new RpcApi.NameAndType
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
                                                throw new NotImplementedException($"Unexpected method type: {method.GetType().Name}");
                                        }
                                        gen *= "CancellationToken cancel_token);";
                                    });
                                    gen += $"public required {method.Name}Handler On{method.Name} {{ get; init; }}";
                                }
                            });
                            gen += $"public static void ProcessClient(ProcessClientConfig config)";
                            gen.AddBlock(gen =>
                            {
                                gen += $"var connection = new RpcApiUtils.Connection(config.Socket, config.CancelToken);";
                                gen += $"var keep_connection_open = false;";
                                gen += $"using var connection_disposer = new LambdaDisposable(() =>";
                                gen.AddBlock(gen =>
                                {
                                    gen += $"if (keep_connection_open)";
                                    gen.AddTab(gen =>
                                    {
                                        gen += $"return;";
                                    });
                                    gen += $"connection.Dispose();";
                                }, "{", "});");
                                gen += $"var client_cmd = connection.ReadMessage((br, _) => br.ReadEnum<EClientCommand>());";
                                gen += $"switch (client_cmd)";
                                gen.AddBlock(gen =>
                                {
                                    foreach (var method in methods)
                                    {
                                        gen += $"case EClientCommand.{method.Name}:";
                                        gen.AddBlock(gen =>
                                        {
                                            void GenReadParameters(RpcApi.NameAndType[] parameters)
                                            {
                                                if (parameters.Length == 0)
                                                    return;
                                                gen.AddLine(gen =>
                                                {
                                                    gen *= "var ";
                                                    gen.AddSeqWithBrackets(parameters, (gen, param) =>
                                                    {
                                                        gen *= param.Name;
                                                    }, ", ", "(", ")");
                                                    gen *= " = connection.ReadMessage((br, _) =>";
                                                });
                                                gen.AddBlock(gen =>
                                                {
                                                    foreach (var parameter in parameters)
                                                        gen += $"var {parameter.Name} = br.ReadData<{parameter.Type}>();";
                                                    gen.AddLine(gen =>
                                                    {
                                                        gen *= "return ";
                                                        gen.AddSeqWithBrackets(parameters, (gen, param) =>
                                                        {
                                                            gen *= param.Name;
                                                        }, ", ", "(", ")");
                                                        gen *= ";";
                                                    });
                                                }, "{", "});");
                                            }

                                            switch (method)
                                            {
                                                case RpcApi.MethodOneOff one_off_method:
                                                    GenReadParameters(one_off_method.Parameters);
                                                    gen += $"connection.ReportErrorsWhile(() =>";
                                                    gen.AddBlock(gen =>
                                                    {
                                                        gen.AddLine(gen =>
                                                        {
                                                            if (one_off_method.ReturnType is not null)
                                                                gen *= "var result = ";
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
                                                            gen += $"connection.WriteMessage((bw, _) => bw.WriteData(result));";
                                                    }, "{", "});");
                                                    break;
                                                case RpcApi.MethodStreamed streamed_method:
                                                    GenReadParameters(streamed_method.NonStreamedParameters);
                                                    gen += $"connection.ReportErrorsWhile(() =>";
                                                    gen.AddBlock(gen =>
                                                    {
                                                        gen.AddLine(gen =>
                                                        {
                                                            gen *= "var ";
                                                            var all_param_names = streamed_method.ReturnedValues.Select(p => p.Name).Append("enumerable_source").ToArray();
                                                            gen.AddSeqWithBrackets(all_param_names, (gen, param_name) =>
                                                            {
                                                                gen *= param_name;
                                                            }, ", ", "(", ")");
                                                            gen *= " = config.On";
                                                            gen *= streamed_method.Name;
                                                            gen *= ".Invoke(";
                                                            foreach (var parameter in streamed_method.NonStreamedParameters)
                                                            {
                                                                gen *= parameter.Name;
                                                                gen *= ", ";
                                                            }
                                                            gen *= "config.CancelToken);";
                                                        });
                                                        if (streamed_method.ReturnedValues.Length != 0)
                                                        {
                                                            gen += $"connection.WriteMessage((bw, _) =>";
                                                            gen.AddBlock(gen =>
                                                            {
                                                                foreach (var param in streamed_method.ReturnedValues)
                                                                    gen += $"bw.WriteData({param.Name});";
                                                            }, "{", "});");
                                                        }
                                                        gen += $"enumerable_source.Subscribe(connection);";
                                                    }, "{", "});");
                                                    gen += $"keep_connection_open = true;";
                                                    break;
                                                default:
                                                    throw new NotImplementedException($"Unexpected method type: {method.GetType().Name}");
                                            }
                                            gen += $"break;";
                                        });
                                    }
                                    gen += $"default:";
                                    gen.AddTab(gen =>
                                    {
                                        gen += $"throw new NotImplementedException($\"Unknown client command: {{client_cmd}}\");";
                                    });
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
                                            gen *= ") => ClientConnector.Connect((conn, _) =>";
                                        });
                                        gen.AddBlock(gen =>
                                        {
                                            gen += $"conn.WriteMessage((bw, _) => bw.WriteEnum(EClientCommand.{one_off_method.Name}));";
                                            if (one_off_method.Parameters.Length != 0)
                                            {
                                                gen += $"conn.WriteMessage((bw, _) =>";
                                                gen.AddBlock(gen =>
                                                {
                                                    foreach (var parameter in one_off_method.Parameters)
                                                        gen += $"bw.WriteData({parameter.Name});";
                                                }, "{", "});");
                                            }
                                            if (one_off_method.ReturnType is { } ret_type)
                                                gen += $"return conn.ReadMessage((br, _) => br.ReadData<{ret_type}>());";
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
                                            var all_params = streamed_method.NonStreamedParameters
                                                .Append(streamed_method.StreamCallbackParameter)
                                                .Append(new() { Name=streamed_method.ReadCancelTokenParameterName, Type="System.Threading.CancellationToken" });
                                            gen.AddSeq(all_params, (gen, param) =>
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
                                            gen += $"void ThreadProc() => Err.HandleDuring(() => ClientConnector.Connect((conn, _) =>";
                                            gen.AddBlock(gen =>
                                            {
                                                gen += $"conn.WriteMessage((bw, _) => bw.WriteEnum(EClientCommand.{streamed_method.Name}));";
                                                if (streamed_method.NonStreamedParameters.Length != 0)
                                                {
                                                    gen += $"conn.WriteMessage((bw, _) =>";
                                                    gen.AddBlock(gen =>
                                                    {
                                                        foreach (var parameter in streamed_method.NonStreamedParameters)
                                                            gen += $"bw.WriteData({parameter.Name});";
                                                    }, "{", "});");
                                                }
                                                if (streamed_method.ReturnedValues.Length != 0)
                                                {
                                                    gen.AddLine(gen =>
                                                    {
                                                        gen *= "var ";
                                                        gen.AddSeqWithBrackets(streamed_method.ReturnedValues, (gen, param) =>
                                                        {
                                                            gen *= param.Name;
                                                        }, ", ", "(", ")");
                                                        gen *= " = conn.ReadMessage((br, _) =>";
                                                    });
                                                    gen.AddBlock(gen =>
                                                    {
                                                        foreach (var param in streamed_method.ReturnedValues)
                                                            gen += $"var {param.Name} = br.ReadData<{param.Type}>();";
                                                        gen.AddLine(gen =>
                                                        {
                                                            gen *= "return ";
                                                            gen.AddSeqWithBrackets(streamed_method.ReturnedValues, (gen, param) =>
                                                            {
                                                                gen *= param.Name;
                                                            }, ", ", "(", ")");
                                                            gen *= ";";
                                                        });
                                                    }, "{", "});");
                                                }
                                                gen += $"var enumerable = new {nameof(RpcEnumerable<>)}<{streamed_method.StreamedItemType}>(conn, {streamed_method.ReadCancelTokenParameterName});";
                                                gen.AddLine(gen =>
                                                {
                                                    gen *= streamed_method.StreamCallbackParameter.Name;
                                                    gen *= ".Invoke(";
                                                    foreach (var param in streamed_method.ReturnedValues)
                                                    {
                                                        gen *= param.Name;
                                                        gen *= ", ";
                                                    }
                                                    gen *= "enumerable)";
                                                    if (streamed_method.CallbackReturnsTask)
                                                        gen *= ".GetAwaiter().GetResult()";
                                                    gen *= ";";
                                                });
                                            }, "{", "}));");
                                        });
                                        break;
                                    }
                                    default:
                                        throw new NotImplementedException($"Unexpected method type: {method.GetType().Name}");
                                }
                                gen += $"";
                            }
                        });
                    });

                    context.AddSource($"{containing_type.Name}_{nameof(RpcApi)}.g.cs", SourceText.From(source_code, GenConstants.Encoding));
                }
            }
            catch (Exception ex)
            {
                throw new Exception(ex.ToString().Replace("\r\n", " ").Replace("\n", " "));
            }
        });

        #endregion

        #region DataStash

        var data_stash_types = context.SyntaxProvider.ForAttributeWithMetadataName(
            typeof(AutoDataStashAttribute).FullName!,
            (node, _) => true,
            (context, _) => (type: (INamedTypeSymbol)context.TargetSymbol, auto_gen_attrib: context.Attributes.Single(a => a.AttributeClass!.Name == nameof(AutoDataStashAttribute)))
        );
        context.RegisterSourceOutput(data_stash_types, (context, gen_item) =>
        {
            try
            {
                //TODO I'm not using auto_gen_attrib anymore
                var (data_stash_type, auto_gen_attrib) = gen_item;
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

                var file_blocks = new Dictionary<String, DataStash.BlockInfo>();
                var closable_typed_models = new Dictionary<String, (INamedTypeSymbol key_type, INamedTypeSymbol model_type)>();
                var typed_content_current_version = default(Int32?);
                var typed_content_old_versions = new Dictionary<Int32, (INamedTypeSymbol model_type, Int32 model_version)[]>();
                var defined_upgrade_paths = new List<(INamedTypeSymbol model_type, INamedTypeSymbol old_file_data_type, INamedTypeSymbol old_parent_model_type)>();
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
                                file_blocks.Add(model_type.Name, new(context, network_data_type, file_data_type, model_type, parent_model_type: null));
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
                                file_blocks.Add(model_type.Name, new(context, network_data_type, file_data_type, model_type, parent_model_type));
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
                            case nameof(DataStash<,>.ITypedContentWithBlockUpgradePath<,,>):
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
                                var model_type = (INamedTypeSymbol)impl_type.TypeArguments[0];
                                var old_file_data_type = (INamedTypeSymbol)impl_type.TypeArguments[1];
                                var old_parent_model_type = (INamedTypeSymbol)impl_type.TypeArguments[2];
                                defined_upgrade_paths.Add((model_type, old_file_data_type, old_parent_model_type));
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
                foreach (var attrib in typed_content_type.GetAttributes())
                {
                    var args = attrib.NamedArguments.ToDictionary(kv => kv.Key, kv => kv.Value);
                    switch (attrib.AttributeClass!.Name)
                    {
                        case nameof(DataStash<,>.TypedContentAttribute):
                        {
                            if (!args.TryGetValue(nameof(DataStash<,>.TypedContentAttribute.Version), out var current_version_const))
                            {
                                data_stash_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS010",
                                    title: "Missing Version argument on [TypedContent] attribute",
                                    messageFormat: "Typed content type '{0}' has a [TypedContent] attribute without a Version argument",
                                    DiagnosticSeverity.Error,
                                    args: [typed_content_type.ToDisplayString()]
                                );
                                continue;
                            }
                            typed_content_current_version = (Int32)current_version_const.Value!;
                            break;
                        }
                        case nameof(DataStash<,>.TypedContentOldVersionAttribute):
                        {
                            if (!args.TryGetValue(nameof(DataStash<,>.TypedContentOldVersionAttribute.Version), out var old_version_const))
                            {
                                data_stash_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS011",
                                    title: "Missing Version argument on [TypedContentOldVersion] attribute",
                                    messageFormat: "Typed content type '{0}' has a [TypedContentOldVersion] attribute without a Version argument",
                                    DiagnosticSeverity.Error,
                                    args: [typed_content_type.ToDisplayString()]
                                );
                                continue;
                            }
                            if (!args.TryGetValue(nameof(DataStash<,>.TypedContentOldVersionAttribute.ExpectedModelTypes), out var expected_model_types_const))
                            {
                                data_stash_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS012",
                                    title: "Missing ExpectedModelTypes argument on [TypedContentOldVersion] attribute",
                                    messageFormat: "Typed content type '{0}' has a [TypedContentOldVersion] attribute without an ExpectedModelTypes argument",
                                    DiagnosticSeverity.Error,
                                    args: [typed_content_type.ToDisplayString()]
                                );
                                continue;
                            }
                            if (!args.TryGetValue(nameof(DataStash<,>.TypedContentOldVersionAttribute.ExpectedModelVersions), out var expected_model_versions_const))
                            {
                                data_stash_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS013",
                                    title: "Missing ExpectedModelVersions argument on [TypedContentOldVersion] attribute",
                                    messageFormat: "Typed content type '{0}' has a [TypedContentOldVersion] attribute without an ExpectedModelVersions argument",
                                    DiagnosticSeverity.Error,
                                    args: [typed_content_type.ToDisplayString()]
                                );
                                continue;
                            }
                            var old_version = (Int32)old_version_const.Value!;
                            var expected_model_types = expected_model_types_const.Values.ToArray(c => (INamedTypeSymbol?)c.Value ?? throw null!);
                            var expected_model_versions = expected_model_versions_const.Values.ToArray(c => (Int32)c.Value!);
                            if (expected_model_types.Length != expected_model_versions.Length)
                            {
                                data_stash_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS014",
                                    title: "Mismatched ExpectedModelTypes and ExpectedModelVersions lengths on [TypedContentOldVersion] attribute",
                                    messageFormat: "Typed content type '{0}' has a [TypedContentOldVersion] attribute with mismatched lengths for ExpectedModelTypes ({1}) and ExpectedModelVersions ({2})",
                                    DiagnosticSeverity.Error,
                                    args: [typed_content_type.ToDisplayString(), expected_model_types.Length, expected_model_versions.Length]
                                );
                                continue;
                            }
                            typed_content_old_versions.Add(old_version, expected_model_types.Zip(expected_model_versions, (model_type, model_version) => (model_type, model_version)).ToArray());
                            break;
                        }
                    }
                }
                if (typed_content_current_version is null)
                {
                    data_stash_type.ReportOnAllDeclaringSyntax(
                        context,
                        id: "DS010",
                        title: "Missing [TypedContent] attribute on typed content type",
                        messageFormat: "Typed content type '{0}' should have a [TypedContent] attribute with a current version number",
                        DiagnosticSeverity.Error,
                        args: [typed_content_type.ToDisplayString()]
                    );
                    return;
                }
                foreach (var block_info in file_blocks.Values)
                {
                    if (!block_info.ModelType.ValidateAsPartialClass(context, "DS006", "DS007"))
                        return;
                    if (!SymbolEqualityComparer.Default.Equals(block_info.ModelType.ContainingType, typed_content_type))
                    {
                        data_stash_type.ReportOnAllDeclaringSyntax(
                            context,
                            id: "DS005",
                            title: "Invalid declaration of DataStash typed content type",
                            messageFormat: "DataStash content model type '{0}' should be declared nested in its DataStash typed content type '{1}'",
                            DiagnosticSeverity.Error,
                            args: [block_info.ModelType.ToDisplayString(), typed_content_type.ToDisplayString()]
                        );
                        return;
                    }
                    if (!block_info.FileDataType.GetAttributes().Any(attrib => attrib.AttributeClass?.Name == nameof(VersionedDataAttribute)))
                    {
                        block_info.FileDataType.ReportOnAllDeclaringSyntax(
                            context,
                            id: "DS009",
                            title: "Missing [VersionedData] attribute on file data type",
                            messageFormat: "File data type '{0}' should have a [VersionedData] attribute",
                            DiagnosticSeverity.Warning,
                            args: [block_info.FileDataType.ToDisplayString()]
                        );
                    }
                    foreach (var old_version in block_info.OldVersions.Keys)
                    {
                        if (typed_content_old_versions.Values.Any(a => a.Any(v => SymbolEqualityComparer.Default.Equals(v.model_type, block_info.ModelType) && v.model_version == old_version)))
                            continue;
                        block_info.ModelType.ReportOnAllDeclaringSyntax(
                            context,
                            id: "DS015",
                            title: "Unused old model version",
                            messageFormat: "Model type '{0}' has an old version {1} that is not referenced in any [TypedContentOldVersion] attribute on the typed content type '{2}'",
                            DiagnosticSeverity.Warning,
                            args: [block_info.ModelType.ToDisplayString(), old_version, typed_content_type.ToDisplayString()]
                        );
                    }
                    var upgrade_paths = defined_upgrade_paths.Where(p => SymbolEqualityComparer.Default.Equals(p.model_type, block_info.ModelType)).ToList();
                    var upgradable_file_data_type_names = upgrade_paths.Select(p => p.old_file_data_type.Name).ToHashSet();
                    var defined_file_data_type_names = block_info.OldVersions.Values.Select(v => v.OldFileDataType.Name).ToHashSet();
                    foreach (var file_data_type_name in upgradable_file_data_type_names)
                    {
                        if (defined_file_data_type_names.Contains(file_data_type_name))
                            continue;
                        block_info.ModelType.ReportOnAllDeclaringSyntax(
                            context,
                            id: "DS016",
                            title: "Upgrade path defined for non-existent old version",
                            messageFormat: "Model type '{0}' has an upgrade path defined for old file data type '{1}', but no [TypedModelOldVersion] attribute exists for that file data type in the typed content type '{2}'",
                            DiagnosticSeverity.Warning,
                            args: [block_info.ModelType.ToDisplayString(), file_data_type_name, typed_content_type.ToDisplayString()]
                        );
                    }
                    foreach (var file_data_type_name in defined_file_data_type_names)
                    {
                        if (upgradable_file_data_type_names.Contains(file_data_type_name))
                            continue;
                        block_info.ModelType.ReportOnAllDeclaringSyntax(
                            context,
                            id: "DS017",
                            title: "Old version defined without upgrade path",
                            messageFormat: "Model type '{0}' has an old version defined for file data type '{1}', but no upgrade path is defined for that file data type in the typed content type '{2}'",
                            DiagnosticSeverity.Error,
                            args: [block_info.ModelType.ToDisplayString(), file_data_type_name, typed_content_type.ToDisplayString()]
                        );
                    }
                    //TODO Also ensure parent model types are equal between upgrade path and old version definition?
                }
                var all_parent_model_names = file_blocks.Values
                    .Select(b => b.ParentModelType)
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

                    #region EBlockKind

                    void GenBlockKindEnum(String suffix, IEnumerable<INamedTypeSymbol> model_types)
                    {
                        gen += $"file enum EBlockKind{suffix} : Byte";
                        gen.AddBlock(gen =>
                        {
                            gen += $"Invalid = 0,";
                            foreach (var model_type in model_types)
                                gen += $"{model_type.Name},";
                        });
                        gen += $"";
                    }

                    GenBlockKindEnum(suffix: "", file_blocks.Values.Select(b => b.ModelType));

                    foreach (var kv in typed_content_old_versions)
                    {
                        var old_version = kv.Key;
                        var models = kv.Value;
                        GenBlockKindEnum(suffix: $"_v{old_version}", models.Select(b => b.model_type));
                    }

                    #endregion

                    gen += $"{data_stash_type.DeclaredAccessibility.ConvertToGenStr()} partial class {data_stash_type.Name}";
                    gen.AddBlock(gen =>
                    {
                        gen += $"";

                        gen += $"protected override Int32 CurrentTypedContentVersion => {typed_content_current_version.Value};";
                        gen += $"protected override Int32 PreVersioningTypedContentVersion => {typed_content_old_versions.Keys.DefaultIfEmpty(typed_content_current_version.Value).Min()};";
                        gen += $"";

                        #region Network methods

                        foreach (var block_info in file_blocks.Values)
                        {
                            var network_data_type = block_info.NetworkDataType;
                            var model_type = block_info.ModelType;
                            var parent_model_type = block_info.ParentModelType;

                            gen += $"public {model_type.ToDisplayString()} Add{model_type.Name}({network_data_type.ToDisplayString()} network_data)";
                            gen.AddBlock(gen =>
                            {
                                if (parent_model_type is not null)
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
                                gen += $"model.CloseContents();";
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

                            gen += $"public void ApplyBlock(VersionInfo version, CommonTypedModelInfo common_info, ReadContext context)";
                            gen.AddBlock(gen =>
                            {
                                gen += $"switch (version.TypedContentVersion)";
                                gen.AddBlock(gen =>
                                {
                                    void GenVersionHandling(
                                        Int32 version, String block_kind_enum_name,
                                        IEnumerable<(Boolean need_upgrade, INamedTypeSymbol model_type, INamedTypeSymbol file_data_type, INamedTypeSymbol? parent_model_type)> model_list)
                                    {
                                        gen += $"case {version}:";
                                        gen.AddBlock(gen =>
                                        {
                                            gen += $"var command = context.ReadCommand<{block_kind_enum_name}>();";
                                            gen += $"switch (command)";
                                            gen.AddBlock(gen =>
                                            {
                                                foreach (var (need_upgrade, model_type, file_data_type, parent_model_type) in model_list)
                                                {
                                                    gen += $"case {block_kind_enum_name}.{model_type.Name}:";
                                                    gen.AddBlock(gen =>
                                                    {
                                                        if (parent_model_type is not null)
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
                                                            gen *= "this.ReadBlock";
                                                            if (need_upgrade)
                                                                gen *= "OldVersion";
                                                            gen *= "(common_info, ";
                                                            if (parent_model_type is not null)
                                                                gen *= "parent, ";
                                                            gen *= "file_data";
                                                            if (!need_upgrade)
                                                                gen *= ", is_new: false";
                                                            gen *= ");";
                                                        });
                                                        gen += $"break;";
                                                    });
                                                }
                                                gen += $"default:";
                                                gen.AddTab(gen =>
                                                {
                                                    gen += $"throw new InvalidDataException($\"{{context.Description}}: Invalid block kind for content version {version}: {{command}}\");";
                                                });
                                            });
                                            gen += $"break;";
                                        });
                                    }

                                    GenVersionHandling(typed_content_current_version.Value, "EBlockKind", file_blocks.Values.Select(b => (false, b.ModelType, b.FileDataType, b.ParentModelType)));

                                    foreach (var kv in typed_content_old_versions.OrderByDescending(kv => kv.Key))
                                    {
                                        var old_content_version = kv.Key;
                                        var old_model_list = kv.Value;
                                        var need_upgrade = old_model_list.ToDictionary(t => t.model_type.Name, t => t.model_version != file_blocks[t.model_type.Name].ModelVersion);

                                        GenVersionHandling(old_content_version, $"EBlockKind_v{old_content_version}", old_model_list.Select(t =>
                                        {
                                            var (model_type, model_version) = t;
                                            var block_info = file_blocks[model_type.Name];
                                            var need_upgrade = model_version != block_info.ModelVersion;
                                            var file_data_type = need_upgrade ? block_info.OldVersions[model_version].OldFileDataType : block_info.FileDataType;
                                            var parent_model_type = need_upgrade ? block_info.OldVersions[model_version].OldParentModelType : block_info.ParentModelType;
                                            return (need_upgrade, model_type, file_data_type, parent_model_type);
                                        }));
                                    }

                                    gen += $"default:";
                                    gen.AddTab(gen =>
                                    {
                                        gen += $"throw new InvalidDataException($\"{{context.Description}}: Invalid content version: {{version.TypedContentVersion}}\");";
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
                                gen += $"var old_model_list = block_locations.ToArray(GetModelByLocation);";
                                gen += $"Prompt.Notify($\"{{old_model_list.Length}} old_model_list are preventing sealing of {{file_group_description}}: {{old_model_list.JoinToString(\"; \")}}\");";
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

                            foreach (var block_info in file_blocks.Values)
                            {
                                var model_type = block_info.ModelType;
                                var parent_model_type = block_info.ParentModelType;

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
                                    if (parent_model_type is not null)
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

                                foreach (var block_info in file_blocks.Values)
                                {
                                    var model_type = block_info.ModelType;
                                    var parent_model_type = block_info.ParentModelType;

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
                                        gen *= "public void AddBlock(";
                                        gen *= model_type.ToDisplayString();
                                        gen *= " model";
                                        if (all_parent_model_names.Contains(model_type.Name))
                                        {
                                            gen *= ", Action<";
                                            gen *= ResaveContextClassName(model_type.Name);
                                            gen *= "> resave_children";
                                        }
                                        gen *= ")";
                                    });
                                    gen.AddBlock(gen =>
                                    {
                                        gen += $"if (model.CommonInfo.RecordTimeUtc < this.last_record_time_utc)";
                                        gen.AddTab(gen =>
                                        {
                                            gen += $"throw new InvalidOperationException($\"{data_stash_type.Name}.{ResaveContextClassName(container_model_name)}: Cannot write {model_type.Name} block with record time {{model.CommonInfo.RecordTimeUtc}} before last written record time {{this.last_record_time_utc}}\");";
                                        });
                                        gen += $"this.last_record_time_utc = model.CommonInfo.RecordTimeUtc;";
                                        gen.AddLine(gen =>
                                        {
                                            gen *= "this.context.WriteBlock(model.CommonInfo, EBlockKind.";
                                            gen *= model_type.Name;
                                            gen *= ", ";
                                            if (parent_model_type is { })
                                                gen *= "model.Parent?.CommonInfo.Location, ";
                                            else
                                                gen *= "parent_location: null, ";
                                            gen *= "model.ConvertToFileData());";
                                        });
                                        if (all_parent_model_names.Contains(model_type.Name))
                                            gen += $"resave_children.Invoke(new {ResaveContextClassName(model_type.Name)}(this.context, this.last_record_time_utc));";
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
            }
            catch (Exception ex)
            {
                throw new Exception(ex.ToString().Replace("\r\n", " ").Replace("\n", " "));
            }
        });

        #endregion

    }

    private static class RpcApi
    {

        public readonly struct NameAndType
        {
            public required String Name { get; init; }
            public required String Type { get; init; }
        }

        public abstract class Method
        {
            public required String Name { get; init; }
            public required String Accessibility { get; init; }

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
            public required String ReadCancelTokenParameterName { get; init; }
            public required NameAndType[] ReturnedValues { get; init; }
            public required String StreamedItemType { get; init; }
            public required Boolean CallbackReturnsTask { get; init; }
        }

    }

    private static class DataStash
    {

        public readonly struct BlockInfo
        {
            public INamedTypeSymbol NetworkDataType { get; }
            public INamedTypeSymbol FileDataType { get; }
            public INamedTypeSymbol ModelType { get; }
            public INamedTypeSymbol? ParentModelType { get; }
            public Int32 ModelVersion { get; }
            public Dictionary<Int32, ModelOldVersionInfo> OldVersions { get; }

            public BlockInfo(SourceProductionContext context, INamedTypeSymbol network_data_type, INamedTypeSymbol file_data_type, INamedTypeSymbol model_type, INamedTypeSymbol? parent_model_type)
            {
                this.NetworkDataType = network_data_type;
                this.FileDataType = file_data_type;
                this.ModelType = model_type;
                this.ParentModelType = parent_model_type;

                var model_version = default(Int32?);
                var old_versions = new Dictionary<Int32, ModelOldVersionInfo>();
                var seen_file_data_types = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                {
                    file_data_type
                };
                foreach (var attrib in model_type.GetAttributes())
                {
                    var args = attrib.NamedArguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    switch (attrib.AttributeClass!.Name)
                    {
                        case nameof(DataStash<,>.TypedModelAttribute):
                        {
                            if (!args.TryGetValue(nameof(DataStash<,>.TypedModelAttribute.Version), out var model_version_const))
                            {
                                model_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS011",
                                    title: "Missing ModelVersion argument in [TypedModel] attribute",
                                    messageFormat: "Model type '{0}' has a [TypedModel] attribute without a ModelVersion argument",
                                    severity: DiagnosticSeverity.Error,
                                    args: [model_type.ToDisplayString()]
                                );
                                continue;
                            }
                            model_version = (Int32)model_version_const.Value!;
                            break;
                        }
                        case nameof(DataStash<,>.TypedModelOldVersionAttribute):
                        {
                            if (!args.TryGetValue(nameof(DataStash<,>.TypedModelOldVersionAttribute.Version), out var old_version_const))
                            {
                                model_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS012",
                                    title: "Missing Version argument in [TypedModelOldVersion] attribute",
                                    messageFormat: "Model type '{0}' has a [TypedModelOldVersion] attribute without a Version argument",
                                    severity: DiagnosticSeverity.Error,
                                    args: [model_type.ToDisplayString()]
                                );
                                continue;
                            }
                            if (!args.TryGetValue(nameof(DataStash<,>.TypedModelOldVersionAttribute.OldFileDataType), out var old_file_data_type_const))
                            {
                                model_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS013",
                                    title: "Missing OldFileDataType argument in [TypedModelOldVersion] attribute",
                                    messageFormat: "Model type '{0}' has a [TypedModelOldVersion] attribute without an OldFileDataType argument",
                                    severity: DiagnosticSeverity.Error,
                                    args: [model_type.ToDisplayString()]
                                );
                                continue;
                            }
                            if (!args.TryGetValue(nameof(DataStash<,>.TypedModelOldVersionAttribute.OldParentModel), out var old_parent_model_type_const))
                            {
                                model_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS014",
                                    title: "Missing OldParentModelType argument in [TypedModelOldVersion] attribute",
                                    messageFormat: "Model type '{0}' has a [TypedModelOldVersion] attribute without an OldParentModelType argument",
                                    severity: DiagnosticSeverity.Error,
                                    args: [model_type.ToDisplayString()]
                                );
                                continue;
                            }
                            var old_version = (Int32)old_version_const.Value!;
                            var old_file_data_type = (INamedTypeSymbol?)old_file_data_type_const.Value ?? throw new InvalidOperationException("OldFileDataType is null");
                            var old_parent_model_type = (INamedTypeSymbol?)old_parent_model_type_const.Value ?? throw new InvalidOperationException("OldParentModelType is null");
                            if (!seen_file_data_types.Add(old_file_data_type))
                            {
                                model_type.ReportOnAllDeclaringSyntax(
                                    context,
                                    id: "DS015",
                                    title: "Duplicate OldFileDataType in [TypedModelOldVersion] attributes",
                                    messageFormat: "Model type '{0}' has [TypedModelOldVersion] attributes with non-unique OldFileDataType '{1}'",
                                    severity: DiagnosticSeverity.Warning,
                                    args: [model_type.ToDisplayString(), old_file_data_type.ToDisplayString()]
                                );
                            }
                            old_versions.Add(old_version, new()
                            {
                                Version = old_version,
                                OldFileDataType = old_file_data_type,
                                OldParentModelType = old_parent_model_type,
                            });
                            break;
                        }
                    }
                }

                if (model_version is null)
                {
                    model_version = 1;
                    model_type.ReportOnAllDeclaringSyntax(
                        context,
                        id: "DS010",
                        title: "Missing [ModelVersion] attribute on model type",
                        messageFormat: "Model type '{0}' should have a [ModelVersion] attribute, defaulting to version 1",
                        severity: DiagnosticSeverity.Warning,
                        args: [model_type.ToDisplayString()]
                    );
                }
                this.ModelVersion = model_version.Value;
                this.OldVersions = old_versions;
            }

        }

        public readonly struct ModelOldVersionInfo
        {
            public required Int32 Version { get; init; }
            public required INamedTypeSymbol OldFileDataType { get; init; }
            public required INamedTypeSymbol OldParentModelType { get; init; }
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
