using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SystemMind.Controls;
using SystemMind.ViewModels;

namespace SystemMind;

public partial class MainWindow : Window
{
    private TreemapControl? _treemap;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _treemap = this.FindControl<TreemapControl>("TreemapDisplay");
        if (_treemap != null)
        {
            _treemap.SizeChanged += (s, e) =>
            {
                if (DataContext is MainViewModel vm && e.NewSize.Width > 10 && e.NewSize.Height > 10)
                {
                    vm.StorageExplorer.UpdateTreemapSize(e.NewSize.Width, e.NewSize.Height);
                }
            };
        }
    }
}
