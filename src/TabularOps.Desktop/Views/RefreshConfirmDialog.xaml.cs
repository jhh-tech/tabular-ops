using System.Windows;

namespace TabularOps.Desktop.Views;

public sealed record TableRefreshInfo(string TableName, IReadOnlyList<string> Partitions);

public partial class RefreshConfirmDialog : Window
{
    public string Summary { get; }
    public string RefreshTypeName { get; }
    public IReadOnlyList<TableRefreshInfo> Tables { get; }

    public RefreshConfirmDialog(
        IReadOnlyList<(string Table, string Partition)> partitions,
        string refreshTypeName)
    {
        var grouped = partitions
            .GroupBy(p => p.Table, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TableRefreshInfo(g.Key, g.Select(p => p.Partition).ToList()))
            .ToList();

        int tableCount = grouped.Count;
        int partitionCount = partitions.Count;

        Summary = tableCount == 1
            ? $"{partitionCount} partition(s) in 1 table"
            : $"{partitionCount} partition(s) across {tableCount} tables";

        RefreshTypeName = refreshTypeName;
        Tables = grouped;

        InitializeComponent();
        DataContext = this;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}
