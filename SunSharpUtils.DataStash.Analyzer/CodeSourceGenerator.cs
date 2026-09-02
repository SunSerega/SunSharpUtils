using System;
using System.Text;

namespace SunSharpUtils.DataStash.Analyzer;

internal sealed class CodeSourceGenerator
{
    private readonly StringBuilder sb = new();
    private Int32 block_depth = 0;

    private CodeSourceGenerator() { }

    public static String Gen(Action<CodeSourceGenerator> act)
    {
        var gen = new CodeSourceGenerator();
        act(gen);
        return gen.sb.ToString();
    }

    public static CodeSourceGenerator operator +(CodeSourceGenerator gen, String str)
    {
        foreach (var line in str.Split('\n'))
        {
            if (!String.IsNullOrEmpty(line))
            {
                gen.sb.Append(new String('\t', gen.block_depth));
                gen.sb.Append(line);
            }
            gen.sb.Append('\n');
        }
        return gen;
    }

    public CodeSourceGenerator AddBlock(Action<CodeSourceGenerator> act, String? open_brace, String? close_brace)
    {
        var gen = this;

        if (open_brace is not null)
            gen += $"{open_brace}";
        gen.block_depth += 1;

        act(gen);

        gen.block_depth -= 1;
        if (close_brace is not null)
            gen += $"{close_brace}";

        return gen;
    }
    public CodeSourceGenerator AddBlock(Action<CodeSourceGenerator> act) => this.AddBlock(act, "{", "}");
    public CodeSourceGenerator AddTab(Action<CodeSourceGenerator> act) => this.AddBlock(act, null, null);

    public CodeSourceGenerator AddLine(Action<CodeLineGenerator> act)
    {
        var gen = this;
        gen += CodeLineGenerator.Gen(act);
        return gen;
    }

}
