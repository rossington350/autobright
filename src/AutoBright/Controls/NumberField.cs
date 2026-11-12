namespace AutoBright.Controls;

/// <summary>
/// A labelled numeric input: a caption, an entry with a unit suffix, and a
/// line underneath that shows either a short hint or, when
/// <see cref="Error"/> is set, a validation message in the error colour.
/// Raises <see cref="Committed"/> when the user presses Enter or leaves the box.
/// </summary>
public sealed class NumberField : ContentView
{
    public static readonly BindableProperty CaptionProperty = BindableProperty.Create(
        nameof(Caption), typeof(string), typeof(NumberField), "",
        propertyChanged: (b, _, n) => ((NumberField)b)._caption.Text = (string)n);

    public static readonly BindableProperty UnitProperty = BindableProperty.Create(
        nameof(Unit), typeof(string), typeof(NumberField), "",
        propertyChanged: (b, _, n) =>
        {
            var field = (NumberField)b;
            field._unit.Text = (string)n;
            field._unit.IsVisible = !string.IsNullOrEmpty((string)n);
        });

    public static readonly BindableProperty HintProperty = BindableProperty.Create(
        nameof(Hint), typeof(string), typeof(NumberField), "",
        propertyChanged: (b, _, _) => ((NumberField)b).UpdateFootnote());

    public static readonly BindableProperty ErrorProperty = BindableProperty.Create(
        nameof(Error), typeof(string), typeof(NumberField), null,
        propertyChanged: (b, _, _) => ((NumberField)b).UpdateFootnote());

    private readonly Label _caption;
    private readonly Entry _entry;
    private readonly Label _unit;
    private readonly Label _footnote;

    public NumberField()
    {
        _caption = new Label { Style = Resource<Style>("FieldCaption") };
        _entry = new Entry { Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.End };
        _entry.Completed += (_, _) => Committed?.Invoke(this, EventArgs.Empty);
        _entry.Unfocused += (_, _) => Committed?.Invoke(this, EventArgs.Empty);
        _unit = new Label
        {
            Style = Resource<Style>("SecondaryText"),
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
            MinimumWidthRequest = 22,
        };
        _footnote = new Label { FontSize = 11, Margin = new Thickness(2, 2, 0, 0), LineBreakMode = LineBreakMode.WordWrap };

        var inputRow = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 6,
        };
        inputRow.Add(_entry, 0);
        inputRow.Add(_unit, 1);

        Content = new VerticalStackLayout { Children = { _caption, inputRow, _footnote } };
        UpdateFootnote();
    }

    /// <summary>Raised when the user presses Enter or the entry loses focus.</summary>
    public event EventHandler? Committed;

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>Validation message to show under the field; null or empty clears it.</summary>
    public string? Error
    {
        get => (string?)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    public string? Text
    {
        get => _entry.Text;
        set => _entry.Text = value;
    }

    private void UpdateFootnote()
    {
        bool hasError = !string.IsNullOrEmpty(Error);
        _footnote.Text = hasError ? Error : Hint;
        _footnote.IsVisible = !string.IsNullOrEmpty(_footnote.Text);

        Color error = Resource<Color>("ErrorColor") ?? Colors.Red;
        if (hasError)
        {
            _footnote.TextColor = error;
            _caption.TextColor = error;
        }
        else
        {
            // Clear the local value so the theme-aware style colour applies again.
            _footnote.ClearValue(Label.TextColorProperty);
            _footnote.Style = Resource<Style>("SecondaryText");
            _caption.ClearValue(Label.TextColorProperty);
        }
    }

    private static T? Resource<T>(string key) where T : class
        => Application.Current?.Resources is { } res && res.TryGetValue(key, out object? value) ? value as T : null;
}
