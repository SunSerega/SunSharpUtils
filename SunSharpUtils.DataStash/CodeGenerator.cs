using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using SunSharpUtils.DataStash.GenExt;
using SunSharpUtils.Ext.Linq;

namespace SunSharpUtils.DataStash.Generators;

//TODO Maybe I can put attributes and stuff into a shared library
// - Shared lib doesn't have intellisense
// - What if I cross-include files from lib project in codegen project?

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

                var source_code = TextGenerator.Gen(gen =>
                {
                    gen += $"using System;\n\n";
                    if (namespace_name is { })
                        gen += $"namespace {namespace_name};\n\n";
                    gen += $"partial class {containing_type.Name}\n";
                    gen.AddBlock(gen =>
                    {
                        gen.AddSeq(g, add_el: (gen, method_symbol) =>
                        {
                            var method_name = method_symbol.Name;
                            var method_accessibility = method_symbol.DeclaredAccessibility switch
                            {
                                Accessibility.Private => "private",
                                _ => throw new NotImplementedException()
                            };

                            gen += $"{method_accessibility} partial void {method_name}()\n";
                            gen.AddBlock(gen =>
                            {
                                gen += "throw new NotImplementedException();\n";
                            });
                        }, add_sep: gen => gen += "\n");
                    });
                });

                context.AddSource($"{containing_type.Name}_RpcApi.g.cs", SourceText.From(source_code, GenConstants.Encoding));
            }
        });

    }

}
