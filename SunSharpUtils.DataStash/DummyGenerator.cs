using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Resources;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using SunSharpUtils.Ext.Linq;

namespace SunSharpUtils.DataStash.Generators;

[Generator]
[SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1041:Compiler extensions should be implemented in assemblies targeting netstandard2.0", Justification = "I can ensure it only runs on .Net10")]
internal class DummyGenerator : IIncrementalGenerator
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
                    ?? throw new Exception($"Resource reported but cannot be read: {resource_name}");
                var reader = new System.IO.StreamReader(stream);
                var resource_content = reader.ReadToEnd();
                context.AddSource(file_name, SourceText.From(resource_content, GenConstants.Encoding));
            }
        });

        var methods = context.SyntaxProvider.ForAttributeWithMetadataName(
            typeof(DummyGenAttribute).FullName!,
            (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax,
            (context, _) => (IMethodSymbol)context.TargetSymbol
        );
        context.RegisterSourceOutput(methods, (context, symbol) =>
        {
            var method_name = symbol.Name;
            var class_name = symbol.ContainingType.Name;
            var namespace_name = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString();
            var method_accessibility = symbol.DeclaredAccessibility.ToString().ToLower();

            var source_code_parts = new List<String>();
            {
                source_code_parts.Add("using System;");
                if (namespace_name is { })
                    source_code_parts.Add($"namespace {namespace_name};");
                source_code_parts.Add($$"""
                    partial class {{class_name}}
                    {
                        {{method_accessibility}} partial void {{method_name}}()
                        {
                            throw new NotImplementedException();
                        }
                    }
                    """);
            }
            var source_code = source_code_parts.JoinToString("\n\n");

            context.AddSource($"{class_name}_{method_name}_Dummy.g.cs", SourceText.From(source_code, GenConstants.Encoding));
        });

        //throw new System.NotImplementedException();
    }

}
