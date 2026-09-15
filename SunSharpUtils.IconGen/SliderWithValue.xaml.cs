using System;
using System.Windows.Controls;

namespace IconGen;

/// <summary>
/// </summary>
public partial class SliderWithValue : UserControl
{
    private readonly Double conversion_k;
    /// <summary>
    /// </summary>
    public Action? ValueChanged = null;

    /// <summary>
    /// </summary>
    public SliderWithValue(String description, Double min, Double max, Double initial, Double conversion_k)
    {
        this.InitializeComponent();

        this.tb_description.Text = description;
        this.s_value.Minimum = min * conversion_k;
        this.s_value.Maximum = max * conversion_k;
        this.s_value.Value = initial * conversion_k;
        this.conversion_k = conversion_k;

        void update_value_text() =>
            this.tb_value.Text = this.Value.ToString("F3").PadLeft(6);
        update_value_text();
        this.s_value.ValueChanged += (o, e) =>
        {
            update_value_text();
            this.ValueChanged?.Invoke();
        };

    }

    /// <summary>
    /// </summary>
    [Obsolete("For XAML designer only", error: true)]
    public SliderWithValue() { }

    /// <summary>
    /// </summary>
    public Double Value => this.s_value.Value / this.conversion_k;

}
