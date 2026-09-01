using System;
using System.Collections.Generic;
using System.Text;

namespace SunSharpUtils.DataStash.GenExt;

internal sealed class TextGenerator
{
    private readonly StringBuilder sb = new();
    private Int32 block_depth = 0;

    private TextGenerator() { }

    public static String Gen(Action<TextGenerator> act)
    {
        var gen = new TextGenerator();
        act(gen);
        return gen.Value;
    }

    public String Value => this.sb.ToString();

    public static TextGenerator operator +(TextGenerator gen, String str)
    {
        var first_line = true;
        foreach (var line in str.Split('\n'))
        {
            if (!first_line)
                gen.sb.Append('\n');
            if (!String.IsNullOrEmpty(line))
                gen.sb.Append(new String('\t', gen.block_depth));
            gen.sb.Append(line);
            first_line = false;
        }
        return gen;
    }

    public TextGenerator AddBlock(Action<TextGenerator> act)
    {
        var gen = this;

        gen += "{\n";
        gen.block_depth += 1;

        act(gen);

        gen.block_depth -= 1;
        gen += "}\n";

        return gen;
    }

    public void AddSeq<T>(IEnumerable<T> seq, Action<TextGenerator, T> add_el, Action<TextGenerator> add_sep)
    {
        var first_el = true;
        foreach (var item in seq)
        {
            if (!first_el)
                add_sep(this);
            add_el(this, item);
            first_el = false;
        }
    }
    public void AddSeq(IEnumerable<String> seq, String sep) =>
        this.AddSeq(seq, add_el: (gen, el) => gen += el, add_sep: gen => gen += sep);

}
