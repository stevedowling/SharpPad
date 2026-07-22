using Avalonia.Controls;
using SharpPad.Engine;

namespace SharpPad.App;

public partial class ConnectionsWindow : Window
{
    private List<DbConnectionInfo> _connections;

    public ConnectionsWindow()
    {
        InitializeComponent();
        _connections = Workspace.LoadConnections();

        ConnectionsList.SelectionChanged += (_, _) => LoadSelected();
        SaveConnButton.Click += (_, _) => SaveCurrent();
        DeleteButton.Click += (_, _) => DeleteSelected();
        CloseButton.Click += (_, _) => Close();

        ProviderCombo.SelectedIndex = 0;
        RefreshList();
    }

    private void RefreshList()
    {
        ConnectionsList.Items.Clear();
        foreach (var c in _connections)
            ConnectionsList.Items.Add(new ListBoxItem { Content = $"{c.Name} ({c.Provider})", Tag = c.Name });
    }

    private void LoadSelected()
    {
        if (ConnectionsList.SelectedItem is not ListBoxItem { Tag: string name }) return;
        var conn = _connections.FirstOrDefault(c => c.Name == name);
        if (conn is null) return;
        NameBox.Text = conn.Name;
        ConnStringBox.Text = conn.ConnectionString;
        ProviderCombo.SelectedIndex = conn.Provider switch
        {
            "sqlite" => 0, "postgres" => 1, "sqlserver" => 2, "mysql" => 3, _ => 0
        };
    }

    private void SaveCurrent()
    {
        var name = NameBox.Text?.Trim();
        var connString = ConnStringBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(connString)) return;
        var provider = ((ComboBoxItem)ProviderCombo.SelectedItem!).Content!.ToString()!;

        _connections.RemoveAll(c => c.Name == name);
        _connections.Add(new DbConnectionInfo(name, provider, connString));
        _connections = _connections.OrderBy(c => c.Name).ToList();
        Workspace.SaveConnections(_connections);
        RefreshList();
    }

    private void DeleteSelected()
    {
        if (ConnectionsList.SelectedItem is not ListBoxItem { Tag: string name }) return;
        _connections.RemoveAll(c => c.Name == name);
        Workspace.SaveConnections(_connections);
        RefreshList();
        NameBox.Text = "";
        ConnStringBox.Text = "";
    }
}
