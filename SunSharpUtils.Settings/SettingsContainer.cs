using System;

using System.IO;
using System.Text;

using System.Linq;
using System.Collections.Generic;

using System.Reflection;
using System.Linq.Expressions;

using SunSharpUtils.Threading;

namespace SunSharpUtils.Settings;

// On why tokens need to exist:
//
// They allow storing the default value and saver for each field in a static dictionary
// Attributes could do the same, but that would be junky
//
// They also allow comparing main and backup files without hacks like initializing 2 instances of GlobalSettings class
// Handling this without tokens would require loading at least the backup file into a dictionary first, to handle missing (default) and duplicate (overriden) keys

/// <summary>
/// Throws when user aborts loading settings when prompted to fix inconsistencies
/// </summary>
public sealed class SettingsLoadUserAbortedException() : Exception() { }

/// <summary>
/// Saves and loads settings of arbitrary structure to and from text files
/// Provides incremental saving and loading and maintains a backup file
/// </summary>
/// <typeparam name="TSelf"></typeparam>
/// <typeparam name="TData"></typeparam>
public abstract class SettingsContainer<TSelf, TData>
    where TSelf : SettingsContainer<TSelf, TData>
    where TData : struct
{
    private readonly String main_save_fname;
    private readonly String back_save_fname;
    private readonly Boolean save_all;
    private TData data;

    /// <summary>
    /// </summary>
    public String GetSettingsFile() => main_save_fname;
    /// <summary>
    /// </summary>
    public String GetSettingsBackupFile() => back_save_fname;
    /// <summary>
    /// </summary>
    public String GetSettingsDir() => Path.GetDirectoryName(main_save_fname)!;

    /// <summary>
    /// Encoding used for settings files
    /// </summary>
    public static Encoding SettingsEncoding { get; set; } = new UTF8Encoding(true);

    /// <summary>
    /// Delay between the last setting change and the next full resave
    /// </summary>
    public static TimeSpan ResaveDelay { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly DelayedMultiUpdater<TSelf> delayed_resave = new(
        container => container.FullResave(),
        $"{nameof(FullResave)} for {typeof(TSelf)} = {nameof(SettingsContainer<TSelf,TData>)}<{typeof(TData)}>",
        is_background: false
    );

    #region Init

    /// <summary>
    /// </summary>
    protected readonly ref struct FieldUpgradeContext(ref TData data, String? value)
    {
        private readonly ref TData data = ref data;

        /// <summary>
        /// Saved value or null if the value was set to default
        /// </summary>
        public String? Value { get; init; } = value;

        /// <summary>
        /// Overrides the value of given field
        /// </summary>
        /// <typeparam name="TField"></typeparam>
        /// <param name="token"></param>
        /// <param name="value"></param>
        public void Set<TField>(FieldToken<TField> token, TField value)
            where TField : IEquatable<TField>?
        {
            token.Set(ref data, value);
        }

    }
    /// <summary>
    /// </summary>
    protected delegate void FieldUpgradeAction(ref FieldUpgradeContext ctx);
    private static readonly Dictionary<String, FieldUpgradeAction> upgrade_actions = [];
    /// <summary>
    /// When field token is not found, this action is called with the saved value or null if the last value was set to default
    /// </summary>
    protected static void RegisterUpgradeAct(String key, FieldUpgradeAction action) => upgrade_actions.Add(key, action);

    private static TData LoadData(String file_path, Boolean save_all, out Boolean need_resave)
    {
        var res = default_data;

        foreach (var token in field_tokens.Values)
            token.SetDefault(ref res);

        void HandleKey(String key, String? value, ref Boolean need_resave)
        {
            if (field_tokens.TryGetValue(key, out var token))
            {
                if (!token.IsDefault(ref res) || value is null)
                    need_resave = true;
                if (value is null)
                    token.SetDefault(ref res);
                else
                    token.Deserialize(ref res, value);
            }
            else if (upgrade_actions.TryGetValue(key, out var action))
            {
                var ctx = new FieldUpgradeContext(ref res, value);
                action(ref ctx);
                need_resave = true;
            }
            else
                throw new FormatException($"Unknown settings field: {key}");
        }

        need_resave = false;
        var key_count = 0;
        foreach (var line in File.ReadLines(file_path))
            try
            {
                key_count++;
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
                    HandleKey(key[..^1], value: null, ref need_resave);
                    continue;
                }

                HandleKey(key, value, ref need_resave);
            }
            catch (FormatException e)
            {
                if (!Prompt.AskYesNo("Settings format issue", $"{file_path}\n\nError parsing line:\n{line}\n\n{e.Message}\n\nIgnore?"))
                    throw new SettingsLoadUserAbortedException();
            }
        if (save_all && key_count != field_tokens.Count)
            need_resave = true;

        return res;
    }

    private static TData default_data = default;
    private static Boolean tokens_initialized = false;
    private static void CheckTokenInitStatus()
    {
        if (tokens_initialized)
            return;

        if (typeof(TSelf).Attributes.HasFlag(TypeAttributes.BeforeFieldInit))
            throw new InvalidOperationException("Settings class must have a static constructor, to make sure static token fields are inited before instance constructor");

        if (typeof(TData).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Length != 0)
            throw new InvalidOperationException($"Settings data structure {typeof(TData)} must not have static fields");
        if (typeof(TData).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Length != 0)
            throw new InvalidOperationException($"Settings data structure {typeof(TData)} must only have public fields");

        var field_names = new HashSet<String>();
        foreach (var fi in typeof(TData).GetFields(BindingFlags.Instance | BindingFlags.Public))
            if (!field_names.Add(fi.Name))
                throw new InvalidOperationException($"Duplicate field name {fi.Name} in data structure {typeof(TData)}");

        if (!field_names.SetEquals(field_tokens.Keys))
        {
            var messages = new List<String>(3)
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
    /// <param name="path">Full file path without extension for settings to store their data</param>
    /// <param name="save_all">If true, all fields will be saved, even if they have default value</param>
    /// <exception cref="SettingsLoadUserAbortedException"></exception>
    protected SettingsContainer(String path, Boolean save_all)
    {
        CheckTokenInitStatus();
        
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        this.main_save_fname = $"{path}.dat";
        this.back_save_fname = $"{path}-Backup.dat";

        this.save_all = save_all;

        if (!File.Exists(main_save_fname))
        {
            if (File.Exists(back_save_fname))
            {
                if (!Prompt.AskYesNo("Settings inconsistency", $"Backup file exists, but main settings file is missing:\n{path}\n\nSimply load backup?"))
                    throw new SettingsLoadUserAbortedException();
                data = LoadData(back_save_fname, save_all, out _);
                FullResave();
            }
            else
            {
                data = default_data;
                File.Create(main_save_fname).Close();
            }
            return;
        }

        if (!File.Exists(back_save_fname))
        {
            data = LoadData(main_save_fname, save_all, out var need_resave);
            if (need_resave)
                FullResave();
            return;
        }

        var main_data = LoadData(main_save_fname, save_all, out _);
        var back_data = LoadData(back_save_fname, save_all, out _);

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
                content_sb.Append(" (default)");
            content_sb.AppendLine(":");
            content_sb.AppendLine(token.Serialize(ref main_data));
            content_sb.AppendLine();
            content_sb.Append("Backup file");
            if (token.IsDefault(ref back_data))
                content_sb.Append(" (default)");
            content_sb.AppendLine(":");
            content_sb.AppendLine(token.Serialize(ref back_data));

            const String P_Main = "Take main";
            const String P_Back = "Take backup";
            const String P_Abort = "Abort";

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
        FullResave();
    }

    #endregion

    #region FieldToken

    private static readonly Dictionary<String, FieldTokenBase> field_tokens = [];

    /// <summary>
    /// </summary>
    protected abstract class FieldTokenBase
    {
        /// <summary>
        /// </summary>
        public abstract String Name { get; }

        internal abstract Boolean IsDefault(ref TData data);
        internal abstract void SetDefault(ref TData res);

        internal abstract Boolean IsEqual(ref TData main_data, ref TData back_data);
        internal abstract void Copy(ref TData from, ref TData to);

        internal abstract String Serialize(ref TData res);
        internal abstract void Deserialize(ref TData res, String v);

    }

    /// <summary>
    /// </summary>
    protected class FieldToken<TField> : FieldTokenBase
        where TField : IEquatable<TField>?
    {
        private delegate TField DGetter(ref TData data);
        private delegate void DSetter(ref TData data, TField value);

        private readonly String name;
        private readonly DGetter getter;
        private readonly DSetter setter;
        private readonly TField def_val;
        private readonly SettingsFieldSaver<TField> saver;

        /// <summary>
        /// </summary>
        public override String Name => name;

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

        private static Boolean Equals(TField a, TField b) =>
            a is null ? b is null : a.Equals(b);

        /// <summary>
        /// </summary>
        public TField Get(ref TData data) => getter(ref data);
        /// <summary>
        /// </summary>
        /// <param name="data"></param>
        /// <param name="value"></param>
        /// <returns>true if value was updated</returns>
        public Boolean Set(ref TData data, TField value)
        {
            if (Equals(value, Get(ref data)))
                return false;
            setter(ref data, value);
            return true;
        }

        /// <summary>
        /// </summary>
        public TField Get(TSelf container) => Get(ref container.data);
        /// <summary>
        /// Sets the value and saves it to the settings file, if it has changed
        /// </summary>
        public void Set(TSelf container, TField value)
        {
            if (!Set(ref container.data, value))
                return;
            if (Equals(value, def_val))
                container.IncrementalDelete(name);
            else
                container.IncrementalSave(name, saver.Serialize(value));
            delayed_resave.TriggerPostpone(container, ResaveDelay);
        }

        internal override Boolean IsDefault(ref TData data) => Equals(getter(ref data), def_val);
        internal override void SetDefault(ref TData res) => setter(ref res, def_val);

        internal override Boolean IsEqual(ref TData data1, ref TData data2) => Equals(getter(ref data1), getter(ref data2));
        internal override void Copy(ref TData from, ref TData to) => setter(ref to, getter(ref from));

        internal override String Serialize(ref TData res) => saver.Serialize(getter(ref res));
        internal override void Deserialize(ref TData res, String v) => setter(ref res, saver.Deserialize(v));

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
        where TField : IEquatable<TField>?
    {
        saver ??= SettingsFieldSaver<TField>.DefaultOrThrow;

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
            static void ThrowFormat(String explanation) => throw new ArgumentException($"Expression must be in the form data=>data.field: {explanation}");
        }

    }

    #endregion

    #region Save

    private readonly Object save_lock = new();

    private void MakeSureBackupExists()
    {
        if (File.Exists(back_save_fname))
            return;
        File.Copy(main_save_fname, back_save_fname);
    }

    private void IncrementalSave(String name, String value)
    {
        using var save_locker = new ObjectLocker(save_lock);
        MakeSureBackupExists();
        File.AppendAllLines(main_save_fname, [$"{name}={value}"], SettingsEncoding);
        File.AppendAllLines(back_save_fname, [$"{name}={value}"], SettingsEncoding);
    }

    private void IncrementalDelete(String name)
    {
        using var save_locker = new ObjectLocker(save_lock);
        MakeSureBackupExists();
        File.AppendAllLines(main_save_fname, [$"{name}!="], SettingsEncoding);
        File.AppendAllLines(back_save_fname, [$"{name}!="], SettingsEncoding);
    }

    private void FullResave()
    {
        using var save_locker = new ObjectLocker(save_lock);
        MakeSureBackupExists();
        var sw = new StreamWriter(main_save_fname, false, SettingsEncoding);

        foreach (var token in field_tokens.Values)
        {
            if (!save_all && token.IsDefault(ref data))
                continue;
            sw.WriteLine($"{token.Name}={token.Serialize(ref data)}");
        }

        sw.Close();
        File.Delete(back_save_fname);
    }

    #endregion

}