using System.Windows;
using System.Windows.Controls;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.controls;

public partial class SecretInput : UserControl
{
    public static readonly DependencyProperty SecretValueProperty = DependencyProperty.Register(
        nameof(SecretValue), typeof(string), typeof(SecretInput),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSecretValueChanged));

    private bool isVisible;
    private bool updatingFromBinding;

    public string SecretValue
    {
        get => (string)GetValue(SecretValueProperty);
        set => SetValue(SecretValueProperty, value);
    }

    public SecretInput()
    {
        InitializeComponent();
        RefreshVisibilityText();
        Loaded += (_, _) => LocalizationService.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    private static void OnSecretValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (SecretInput)sender;
        control.updatingFromBinding = true;
        try
        {
            string value = args.NewValue as string ?? string.Empty;
            control.MaskedInput.Password = value;
            control.VisibleInput.Text = value;
        }
        finally { control.updatingFromBinding = false; }
    }

    private void MaskedInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!updatingFromBinding && !isVisible)
            SetCurrentValue(SecretValueProperty, MaskedInput.Password);
    }

    private void VisibleInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!updatingFromBinding && isVisible)
            SetCurrentValue(SecretValueProperty, VisibleInput.Text);
    }

    private void VisibilityToggle_Click(object sender, RoutedEventArgs e)
    {
        isVisible = !isVisible;
        if (isVisible)
        {
            VisibleInput.Text = MaskedInput.Password;
            VisibleInput.Visibility = Visibility.Visible;
            MaskedInput.Visibility = Visibility.Collapsed;
            VisibleInput.Focus();
            VisibleInput.CaretIndex = VisibleInput.Text.Length;
        }
        else
        {
            MaskedInput.Password = VisibleInput.Text;
            MaskedInput.Visibility = Visibility.Visible;
            VisibleInput.Visibility = Visibility.Collapsed;
            MaskedInput.Focus();
        }
        RefreshVisibilityText();
    }

    private void OnLanguageChanged(string _) => RefreshVisibilityText();

    private void RefreshVisibilityText() =>
        VisibilityToggle.Content = LocalizationService.Get(isVisible ? "Hide" : "Show");
}
