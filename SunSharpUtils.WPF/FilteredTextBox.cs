using System;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
    private String uncommitted_text = "";

    private static readonly Brush b_unedited = Brushes.Transparent;
    private static readonly Brush b_valid = Brushes.YellowGreen;
    private static readonly Brush b_invalid = Brushes.Coral;

    /// <summary>
    /// </summary>
    public delegate Boolean FilterFunc(String text, [NotNullWhen(true)] out T? value);

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
        this.Content = this.tb;

        this.GotKeyboardFocus += (o, e) => Err.Handle(() =>
        {
            Keyboard.Focus(this.tb);
        });

        this.tb.TextChanged += (o, e) => Err.Handle(() =>
        {
            this.Edited = this.tb.Text != this.uncommitted_text;
            if (!this.Edited)
                this.tb.Background = b_unedited;
            else if (filter(this.tb.Text, out _))
                this.tb.Background = b_valid;
            else
                this.tb.Background = b_invalid;
        });

        this.tb.KeyDown += (o, e) => Err.Handle(() =>
        {
            if (e.Key != Key.Escape) return;
            if (this.tb.Text == this.uncommitted_text) return;
            var (sel_s, sel_l) = (this.tb.SelectionStart, this.tb.SelectionLength);
            this.ResetContent(this.uncommitted_text);
            (this.tb.SelectionStart, this.tb.SelectionLength) = (sel_s, sel_l);
            e.Handled = true;
        });

        this.tb.KeyDown += (o, e) => Err.Handle(() =>
        {
            if (e.Key != Key.Enter) return;
            this.TryCommit();
        });

    }

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="valid_enter"></param>
    /// <param name="invalid_enter_tb"></param>
    public FilteredTextBox(FilterFunc filter, Action<T> valid_enter, (String title, String content) invalid_enter_tb)
        : this(filter, valid_enter, () => CustomMessageBox.ShowOK(invalid_enter_tb.title, invalid_enter_tb.content, WPFCommon.CurrentApp?.MainWindow))
    { }

    /// <summary>
    /// </summary>
    public event Action? Committed = null;

    /// <summary>
    /// </summary>
    public Boolean Edited { get; private set; } = false;

    /// <summary>
    /// Sets the state to valid with the given content
    /// </summary>
    /// <param name="content"></param>
    public void ResetContent(String content)
    {
        this.tb.Text = content;
        this.tb.Select(content.Length, 0);
        this.tb.Background = Brushes.Transparent;
        this.Edited = false;
        this.uncommitted_text = content;
    }

    /// <summary>
    /// Tries to commit the current text, parse it and 
    /// </summary>
    /// <returns></returns>
    public Boolean TryCommit()
    {
        if (!this.filter(this.tb.Text, out var v))
        {
            this.invalid_enter();
            return false;
        }

        this.tb.Background = Brushes.Transparent;
        this.Edited = false;
        this.valid_enter(v);
        this.ResetContent(this.tb.Text);

        // Need this event to make sure outside code can reference this textbox in handler (unlike in the valid_enter action)
        Committed?.Invoke();

        return true;
    }

}
