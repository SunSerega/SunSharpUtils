using System;

using System.IO;
using System.Text;

using System.Linq;
using System.Collections.Generic;

using System.Reflection;
using System.Linq.Expressions;

using SunSharpUtils.Threading;

namespace SunSharpUtils.Settings;

/// <summary>
/// Throws when user aborts loading settings when prompted to fix inconsistencies
/// </summary>
public sealed class SettingsLoadUserAbortedException() : Exception() { }

/// <summary>
/// Saves and loads settings of arbitrary structure to and from text files
/// Provides incremental saving and loading and maintains a backup file
/// </summary>
/// <typeparam name="TData"></typeparam>
public abstract class SettingsContainer<TData>
    where TData : struct
{
    private readonly string main_save_fname;
    private readonly string back_save_fname;
    private TData data;

    private static TData default_data = default;
    private static bool tokens_initialized = false;
    private static void CheckTokenInitStatus()
    {
        if (tokens_initialized)
            return;
        
        if (typeof(TData).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Length != 0)
            throw new InvalidOperationException($"Settings data structure {typeof(TData)} must only have public fields");

        var field_names = new HashSet<string>();
        foreach (var fi in typeof(TData).GetFields(BindingFlags.Instance | BindingFlags.Public))
            if (!field_names.Add(fi.Name))
                throw new InvalidOperationException($"Duplicate field name {fi.Name} in data structure {typeof(TData)}");

        if (!field_names.SetEquals(field_tokens.Keys))
        {
            var messages = new List<string>(3)
            {
                "SettingsContainer field mismatch",
            };
            
            var missing = field_names.Except(field_tokens.Keys);
            if (missing.Any())
                messages.Add($"Missing tokens: {missing.JoinToString(", ")}");

            var extra = field_tokens.Keys.Except(field_names);
            if (extra.Any())
                messages.Add($"Extra tokens: {extra.JoinToString(", ")}");

            throw new InvalidOperationException(messages.JoinToString(". "));
        }

        tokens_initialized = true;
    }

    /// <summary>
    /// Deploys settings for the specified path
    /// </summary>
    /// <param name="path"></param>
    /// <exception cref="SettingsLoadUserAbortedException"></exception>
    protected SettingsContainer(string path)
    {
        CheckTokenInitStatus();

        main_save_fname = Path.GetFullPath($"{path}.dat");
        back_save_fname = Path.GetFullPath($"{path}-Backup.dat");

        var main_data = LoadData(main_save_fname);
        var back_data = LoadData(back_save_fname);

        foreach (var token in field_tokens.Values)
        {
            if (token.IsEqual(ref main_data, ref back_data))
                continue;

            var title = "Settings inconsistency";

            var content_sb = new StringBuilder();
            content_sb.AppendLine($"Settings at [{path}] have mismatch of {token.Name} field between main and backup files");
            content_sb.AppendLine();
            content_sb.Append("Main file");
            if (token.IsDefault(ref main_data))
                content_sb.AppendLine(" (default)");
            content_sb.AppendLine(":");
            content_sb.AppendLine(token.Serialize(ref main_data));
            content_sb.AppendLine();
            content_sb.Append("Backup file");
            if (token.IsDefault(ref back_data))
                content_sb.AppendLine(" (default)");
            content_sb.AppendLine(":");
            content_sb.AppendLine(token.Serialize(ref back_data));

            const string P_Main = "Take main";
            const string P_Back = "Take backup";
            const string P_Abort = "Abort";

            var choise = Prompt.AskAny(title, content_sb.ToString(), [P_Main, P_Back, P_Abort]);
            switch (choise)
            {
                case P_Main:
                    break;
                case P_Back:
                    token.Copy(ref back_data, ref main_data);
                    break;
                case P_Abort or null:
                    throw new SettingsLoadUserAbortedException();
                default:
                    throw new NotImplementedException($"Invalid option: {choise}");
            }
        }

        data = main_data;
    }

    /// <summary>
    /// Delay between the last setting change and the next full resave
    /// </summary>
    public static TimeSpan ResaveDelay { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, FieldTokenBase> field_tokens = [];
    private static readonly DelayedMultiUpdater<SettingsContainer<TData>> delayed_updater = new(
        container => container.FullResave(),
        $"{nameof(FullResave)} for {nameof(SettingsContainer<TData>)}<{nameof(TData)}>"
    );

    /// <summary>
    /// </summary>
    protected abstract class FieldTokenBase
    {
        /// <summary>
        /// </summary>
        public abstract string Name { get; }

        internal abstract bool IsEqual(ref TData main_data, ref TData back_data);
        internal abstract void Copy(ref TData from, ref TData to);

        internal abstract bool IsDefault(ref TData data);
        internal abstract string Serialize(ref TData res);

        internal abstract void SetDefault(ref TData res);
        internal abstract void Deserialize(ref TData res, string v);

    }

    /// <summary>
    /// </summary>
    protected class FieldToken<TField> : FieldTokenBase
        where TField : notnull, IEquatable<TField>
    {
        private delegate TField DGetter(ref TData data);
        private delegate void DSetter(ref TData data, TField value);

        private readonly string name;
        private readonly DGetter getter;
        private readonly DSetter setter;
        private readonly TField def_val;
        private readonly SettingsFieldSaver<TField> saver;

        /// <summary>
        /// </summary>
        public override string Name => name;

        /// <summary>
        /// </summary>
        public FieldToken(FieldInfo field, TField def_val, SettingsFieldSaver<TField> saver)
        {
            if (tokens_initialized)
                throw new InvalidOperationException("Field tokens must be defined before the first settings container is created");
            name = field.Name;
            if (name.EndsWith('!'))
                throw new ArgumentException("Field name must not end with '!', as it is reserved for incremental value deletion");
            {
                var param = Expression.Parameter(typeof(TData).MakeByRefType());
                var body = Expression.Field(param, field);
                var lambda = Expression.Lambda<DGetter>(body, param);
                getter = lambda.Compile();
            }
            {
                var param1 = Expression.Parameter(typeof(TData).MakeByRefType());
                var param2 = Expression.Parameter(typeof(TField));
                var body = Expression.Assign(Expression.Field(param1, field), param2);
                var lambda = Expression.Lambda<DSetter>(body, param1, param2);
                setter = lambda.Compile();
            }
            this.def_val = def_val;
            this.saver = saver;
            field_tokens.Add(name, this);
            setter(ref default_data, def_val);
        }

        private static bool Equals(TField a, TField b) =>
            a is null ? b is null : a.Equals(b);

        /// <summary>
        /// </summary>
        public TField Get(SettingsContainer<TData> container) => getter(ref container.data);
        /// <summary>
        /// </summary>
        public void Set(SettingsContainer<TData> container, TField value)
        {
            if (Equals(value, Get(container)))
                return;
            setter(ref container.data, value);
            if (Equals(value, def_val))
                container.IncrementalDelete(name);
            else
                container.IncrementalSave(name, saver.Serialize(value));
            delayed_updater.TriggerPostpone(container, ResaveDelay);
        }

        internal override bool IsEqual(ref TData data1, ref TData data2) => Equals(getter(ref data1), getter(ref data2));
        internal override void Copy(ref TData from, ref TData to) => setter(ref to, getter(ref from));

        internal override bool IsDefault(ref TData data) => Equals(getter(ref data), def_val);
        internal override string Serialize(ref TData res) => saver.Serialize(getter(ref res));

        internal override void SetDefault(ref TData res) => setter(ref res, def_val);
        internal override void Deserialize(ref TData res, string v) => setter(ref res, saver.Deserialize(v));

    }

    /// <summary>
    /// Defines a field token derived from a field getter expression
    /// </summary>
    /// <typeparam name="TField"></typeparam>
    /// <param name="field_getter"></param>
    /// <param name="def_val"></param>
    /// <param name="saver"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">If no saver is specified</exception>
    /// <exception cref="ArgumentException">If expression has the wrong format</exception>
    protected static FieldToken<TField> MakeFieldToken<TField>(Expression<Func<TData, TField>> field_getter, TField def_val, SettingsFieldSaver<TField>? saver = null)
        where TField : notnull, IEquatable<TField>
    {
        saver ??= SettingsFieldSaver<TField>.Default ?? throw new InvalidOperationException($"No default saver for {typeof(TField)}");

        return new FieldToken<TField>(ExtractField(field_getter), def_val, saver);

        static FieldInfo ExtractField(Expression<Func<TData, TField>> field_getter)
        {
            if (field_getter.Parameters.Count != 1)
                ThrowFormat("Expression must have exactly one parameter");
            var param = field_getter.Parameters[0];

            if (field_getter.Body is not MemberExpression member_expr)
                ThrowFormat($"Body is not a member access, but {field_getter.Body.GetType()}");

            if (member_expr.Expression != param)
                ThrowFormat($"Member access is not on the parameter, but {member_expr.Expression}");

            if (member_expr.Member is not FieldInfo field)
                ThrowFormat($"Member is not a field, but {member_expr.Member.GetType()}");

            if (field.FieldType != typeof(TField))
                ThrowFormat($"Field is not of type {typeof(TField)}, but {field.FieldType}");

            return field;
            static void ThrowFormat(string explanation) => throw new ArgumentException($"Expression must be in the form data=>data.field: {explanation}");
        }

    }

    private static TData LoadData(string file_path)
    {
        var res = default_data;
        if (!File.Exists(file_path))
            return res;

        foreach (var token in field_tokens.Values)
            token.SetDefault(ref res);

        foreach (var line in File.ReadLines(file_path))
            try
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2)
                    throw new FormatException($"Settings line must have '=' separator");
                var key = parts[0];
                var value = parts[1];

                if (key.EndsWith('!'))
                {
                    if (value != "")
                        if (!Prompt.AskYesNo("Settings format issue", $"{file_path}\n\nIncremental deletion must have empty value:\n{line}\n\nIgnore?"))
                            throw new SettingsLoadUserAbortedException();
                    GetToken(key[..^1]).SetDefault(ref res);
                    continue;
                }

                GetToken(key).Deserialize(ref res, value);
            }
            catch (FormatException e)
            {
                if (!Prompt.AskYesNo("Settings format issue", $"{file_path}\n\nError parsing line:\n{line}\n\n{e.Message}\n\nIgnore?"))
                    throw new SettingsLoadUserAbortedException();
            }

        return res;

        static FieldTokenBase GetToken(string name)
        {
            if (!field_tokens.TryGetValue(name, out var token))
                throw new FormatException($"Unknown settings field: {name}");
            return token;
        }
    }

    private void IncrementalSave(string name, string value)
    {
        File.AppendAllLines(main_save_fname, [$"{name}={value}"]);
        File.AppendAllLines(back_save_fname, [$"{name}={value}"]);
    }

    private void IncrementalDelete(string name)
    {
        File.AppendAllLines(main_save_fname, [$"{name}!="]);
        File.AppendAllLines(back_save_fname, [$"{name}!="]);
    }

    private void FullResave()
    {
        var sw = new StreamWriter(main_save_fname);

        foreach (var token in field_tokens.Values)
        {
            if (token.IsDefault(ref data))
                continue;
            sw.WriteLine($"{token.Name}={token.Serialize(ref data)}");
        }

        sw.Close();

        File.Copy(main_save_fname, back_save_fname, overwrite: true);
    }

}