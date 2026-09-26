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

    public static CodeLineGenerator operator *(CodeLineGenerator gen, NonFormattableString str)
    {
        gen.sb.Append(str.Value);
        return gen;
    }

    [Obsolete($"Don't use interpolated strings with {nameof(CodeLineGenerator)}")]
    public static CodeLineGenerator operator *(CodeLineGenerator gen, FormattableString f) => gen * f.ToString();

    public readonly struct NonFormattableString
    {
        public required String Value { get; init; }
        public static implicit operator NonFormattableString(String s) => new() { Value = s };
        public static implicit operator NonFormattableString(FormattableString s) => throw new InvalidOperationException($"Using {nameof(NonFormattableString)} requires another overload with type {nameof(FormattableString)}");
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
