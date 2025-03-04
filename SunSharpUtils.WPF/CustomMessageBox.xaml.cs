using System;

using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace SunSharpUtils.WPF;

/// <summary>
/// Like WPF MessageBox, but with customizable buttons
/// </summary>
public partial class CustomMessageBox : Window
{

    /// <summary>
    /// </summary>
    public sealed class OwnerWindowContainer
    {
        internal Window? Value { get; private set; }

        internal OwnerWindowContainer() => Value = WPFCommon.CurrentApp?.MainWindow.IsVisible??false ? WPFCommon.CurrentApp.MainWindow : null;
        internal OwnerWindowContainer(Window? owner) => Value = owner;

        /// <summary>
        /// </summary>
        /// <param name="owner"></param>
        public static implicit operator OwnerWindowContainer(Window? owner) => new(owner);

    }

    /// <summary>
    /// Creates a new CustomMessageBox without showing it
    /// </summary>
    public CustomMessageBox(String title, String? content, OwnerWindowContainer owner, params String[] button_names)
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception e)
        {
            MessageBox.Show($"{content}\n\n{e}", title);
            return;
        }

        if (owner.Value?.IsVisible ?? false)
            Owner = owner.Value;

        KeyDown += (o, e) => Err.Handle(() =>
        {
            if (e.Key == Key.Escape)
                Close();
            else if (e.Key == Key.Enter && button_names.Length<2)
            {
                if (button_names.Length != 0)
                    ChosenOption = button_names[0];
                Close();
            }
            else if (e.Key == Key.C &&  Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(title);
                if (content != null)
                {
                    sb.Append("\n\n");
                    sb.Append(content);
                }
                Clipboard.SetText(sb.ToString());
                Console.Beep();
            }
            else
                return;
            e.Handled = true;
        });

        Title = title;

        if (content is null)
            tb_body.Visibility = Visibility.Collapsed;
        else
            tb_body.Text = content;

        if (button_names.Length == 0)
        {
            sp_buttons.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var button_name in button_names)
        {
            var b = new Button
            {
                Content = button_name,
            };
            b.Click += (o, e) => Err.Handle(() =>
            {
                ChosenOption = button_name;
                Close();
            });
            sp_buttons.Children.Add(b);
        }

    }

    /// <summary>
    /// Text on the button that was selected                                        <br />
    /// If there are no buttons or Escape was pressed, this is null                 <br />
    /// If there was only one button, it could've been selected by pressing Enter   <br />
    /// </summary>
    public String? ChosenOption { get; private set; } = null;

    private static readonly OwnerWindowContainer no_own = new(null);

    /// <summary>
    /// </summary>
    public static String? Show(String title, String? content, OwnerWindowContainer owner, params String[] button_names)
    {
        // If the owner is set, only allow message boxes to be shown from the original window thread
        owner.Value?.Dispatcher.VerifyAccess();

        if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
        {
            String? res = null;
            var thr = new System.Threading.Thread(() => Err.Handle(() =>
                res = Show(title, content, owner, button_names)
            ))
            {
                IsBackground = true,
                Name = $"STA thread for {nameof(CustomMessageBox)}.{nameof(Show)}",
            };
            thr.SetApartmentState(System.Threading.ApartmentState.STA);
            thr.Start();
            thr.Join();
            return res;
        }
        var mb = new CustomMessageBox(title, content, owner, button_names);
        mb.ShowDialog();
        return mb.ChosenOption;
    }
    /// <summary>
    /// </summary>
    public static String? Show(String title, String? content, params String[] button_names) => Show(title, content, no_own, button_names);

    /// <summary>
    /// </summary>
    public static TEnum? Show<TEnum>(String title, String? content, OwnerWindowContainer owner, params TEnum[] options)
        where TEnum : struct, Enum
    {
        if (options.Length == 0) throw new ArgumentException("options.Length == 0");
        var res_name = Show(title, content, owner, Array.ConvertAll(options, e => e.ToString()));
        if (res_name is null) return null;
        return (TEnum)Enum.Parse(typeof(TEnum), res_name);
    }
    /// <summary>
    /// </summary>
    public static TRes? Show<TRes>(String title, String? content, params TRes[] options) where TRes : struct, Enum => Show<TRes>(title, content, no_own, options);

    /// <summary>
    /// </summary>
    public static void ShowOK(String title, String? content, OwnerWindowContainer owner) => Show(title, content, owner, "OK");
    /// <summary>
    /// </summary>
    public static void ShowOK(String title, String? content) => ShowOK(title, content, no_own);

    /// <summary>
    /// </summary>
    public static void ShowOK(String title, OwnerWindowContainer owner) => ShowOK(title, null, owner);
    /// <summary>
    /// </summary>
    public static void ShowOK(String title) => ShowOK(title, no_own);

    /// <summary>
    /// </summary>
    public static Boolean ShowYesNo(String title, String? content, OwnerWindowContainer owner) => "Yes"==Show(title, content, owner, "Yes", "No");
    /// <summary>
    /// </summary>
    public static Boolean ShowYesNo(String title, String? content) => ShowYesNo(title, content, no_own);

}
