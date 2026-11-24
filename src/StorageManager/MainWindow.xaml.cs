using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StorageManager.Models;
using StorageManager.ViewModels;

namespace StorageManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private AnalyzerViewModel Analyzer => ((MainViewModel)DataContext).Analyzer;

    /// <summary>Right-click selects the item under the mouse so the context menu acts on it.</summary>
    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Tree.SelectedItem is FsNode { Kind: NodeKind.File } node)
            Analyzer.OpenInExplorerCommand.Execute(node);
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView { SelectedItem: FsNode node })
            Analyzer.OpenInExplorerCommand.Execute(node);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
