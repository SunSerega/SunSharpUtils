using System;

using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;

namespace SunSharpUtils.WPF;

/// <summary>
/// TextBox with input validation and parsing
/// </summary>
/// <typeparam name="T"></typeparam>
public class FilteredTextBox<T> : ContentControl
{
    private readonly FilterFunc filter;
    private readonly Action<T> valid_enter;
    private readonly Action invalid_enter;

    private readonly TextBox tb = new();
    private string uncommited_text = "";

    private static readonly Brush b_unedited = Brushes.Transparent;
    private static readonly Brush b_valid = Brushes.YellowGreen;
    private static readonly Brush b_invalid = Brushes.Coral;

    /// <summary>
    /// </summary>
    public delegate bool FilterFunc(string text, out T value);

    /// <summary>
    /// </summary>
    /// <param name="filter">(string, out T) => bool</param>
    /// <param name="valid_enter"></param>
    /// <param name="invalid_enter"></param>
    public FilteredTextBox(FilterFunc filter, Action<T> valid_enter, Action invalid_enter)
    {
        this.filter = filter;
        this.valid_enter = valid_enter;
        this.invalid_enter = invalid_enter;
        Content = tb;

        tb.TextChanged += (o, e) => Err.Handle(() =>
        {
            Edited = tb.Text != uncommited_text;
            if (!Edited)
                tb.Background = b_unedited;
            else if (filter(tb.Text, out _))
                tb.Background = b_valid;
            else
                tb.Background = b_invalid;
        });

        tb.KeyDown += (o, e) => Err.Handle(() =>
        {
            if (e.Key != Key.Escape) return;
            if (tb.Text == uncommited_text) return;
            var (sel_s, sel_l) = (tb.SelectionStart, tb.SelectionLength);
            ResetContent(uncommited_text);
            (tb.SelectionStart, tb.SelectionLength) = (sel_s, sel_l);
            e.Handled = true;
        });

        tb.KeyDown += (o, e) => Err.Handle(() =>
        {
            if (e.Key != Key.Enter) return;
            TryCommit();
        });

    }

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="valid_enter"></param>
    /// <param name="invalid_enter_tb"></param>
    public FilteredTextBox(FilterFunc filter, Action<T> valid_enter, (string title, string content) invalid_enter_tb)
        : this(filter, valid_enter, () => CustomMessageBox.ShowOK(invalid_enter_tb.title, invalid_enter_tb.content, WPFCommon.CurrentApp?.MainWindow))
    { }

    /// <summary>
    /// </summary>
    public event Action? Commited = null;

    /// <summary>
    /// </summary>
    public bool Edited { get; private set; } = false;

    /// <summary>
    /// Sets the state to valid with the given content
    /// </summary>
    /// <param name="content"></param>
    public void ResetContent(string content)
    {
        tb.Text = content;
        tb.Background = Brushes.Transparent;
        Edited = false;
        uncommited_text = content;
    }

    /// <summary>
    /// Tries to commit the current text, parse it and 
    /// </summary>
    /// <returns></returns>
    public bool TryCommit()
    {
        if (!filter(tb.Text, out var v))
        {
            invalid_enter();
            return false;
        }

        tb.Background = Brushes.Transparent;
        Edited = false;
        valid_enter(v);
        ResetContent(tb.Text);

        // Need this event to make sure outside code can reference this textbox in handler (unlike in the valid_enter action)
        Commited?.Invoke();

        return true;
    }

}
