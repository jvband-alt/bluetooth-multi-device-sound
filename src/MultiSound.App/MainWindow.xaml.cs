using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiSound.App.Services;

namespace MultiSound.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        // Enter em um campo de texto aplica o valor (ex.: atraso em ms).
        AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == Key.Enter && e.OriginalSource is TextBox tb)
            {
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                tb.SelectAll();
            }
        }), handledEventsToo: true);
    }
}
