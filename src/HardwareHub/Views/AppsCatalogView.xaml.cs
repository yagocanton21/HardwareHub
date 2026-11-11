using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HardwareHub.ViewModels;

namespace HardwareHub.Views;

/// <summary>
/// Tela Programas com kits prontos, categorias, icones PNG oficiais e instalador silencioso em fila.
/// </summary>
public partial class AppsCatalogView : UserControl
{
    public AppsCatalogView()
    {
        InitializeComponent();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        int columns = e.NewSize.Width >= 820 ? 3 : (e.NewSize.Width >= 520 ? 2 : 1);
        SetUniformGridColumns(KitsItemsControl, columns);
        SetUniformGridColumns(ProgramsItemsControl, columns);
        SetUniformGridColumns(OnlineStoreItemsControl, columns);
        SetUniformGridColumns(OnlineWingetItemsControl, columns);
    }

    private static void SetUniformGridColumns(ItemsControl? itemsControl, int columns)
    {
        if (itemsControl == null)
        {
            return;
        }

        var grid = FindVisualChild<UniformGrid>(itemsControl);
        if (grid != null && grid.Columns != columns)
        {
            grid.Columns = columns;
        }
    }

    private void OnProgramCardClick(object sender, MouseButtonEventArgs e)
    {
        // Botoes internos (Abrir pagina / Copiar cupom) tratam o proprio clique.
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) != null)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: AppCardViewModel card } &&
            DataContext is AppsCatalogViewModel vm)
        {
            vm.ToggleProgram(card);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested != null)
            {
                return nested;
            }
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}

