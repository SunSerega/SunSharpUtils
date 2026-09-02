using System;
using System.Collections.Generic;
using System.Text;

namespace SunSharpUtils.DataStash.Analyzer;

internal sealed class CodeLineGenerator
{
    private readonly StringBuilder sb = new();

    public static String Gen(Action<CodeLineGenerator> act)
    {
        var gen = new CodeLineGenerator();
        act(gen);
        return gen.sb.ToString();
    }

    public static CodeLineGenerator operator *(CodeLineGenerator gen, String str)
    {
        gen.sb.Append(str);
        return gen;
    }

    public CodeLineGenerator AddSeq<T>(IEnumerable<T> seq, Action<CodeLineGenerator, T> add_el, Action<CodeLineGenerator> add_sep)
    {
        var first_el = true;
        foreach (var item in seq)
        {
            if (!first_el)
                add_sep(this);
            add_el(this, item);
            first_el = false;
        }
        return this;
    }
    public CodeLineGenerator AddSeq<T>(IEnumerable<T> seq, Action<CodeLineGenerator, T> add_el, String sep) =>
        this.AddSeq(seq, add_el, add_sep: gen => gen *= sep);

}
