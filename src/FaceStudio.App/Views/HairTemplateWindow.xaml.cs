using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Cricket07.FaceStudio.Core;

namespace Cricket07.FaceStudio.Views;

/// <summary>Picks a stock head whose hair mesh will be given to the current head (see <see cref="ShapeTransfer"/>).</summary>
public partial class HairTemplateWindow : Window
{
    private static string TemplateFolder => Path.Combine(AppContext.BaseDirectory, "templates");

    /// <summary>Full path of the chosen template's model.o, set when the user confirms.</summary>
    public string? Result { get; private set; }

    public HairTemplateWindow()
    {
        InitializeComponent();
        Ui.FitToScreen(this);
        Templates.ItemsSource = HairTemplates();
        Templates.SelectedIndex = 0;
    }

    /// <summary>Stock heads in the template library that carry a hair mesh.</summary>
    public static List<Template> HairTemplates()
    {
        var catalog = JsonSerializer.Deserialize<List<Template>>(File.ReadAllText(Path.Combine(TemplateFolder, "catalog.json"))) ?? new();
        return catalog.Where(t => t.Category == "Head" && t.HasHair).ToList();
    }

    /// <summary>The template's model.o, after checking it is the file the catalog recorded.</summary>
    public static string Resolve(Template template)
    {
        string path = ModelDocument.ContainedPath(TemplateFolder, template.Model);
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != template.Sha256)
            throw new IOException("Template fingerprint changed. Reinstall the template library.");
        return path;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (Templates.SelectedItem is not Template template) return;
        try { Result = Resolve(template); DialogResult = true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Add hair"); }
    }
    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => OnAdd(sender, e);
}
