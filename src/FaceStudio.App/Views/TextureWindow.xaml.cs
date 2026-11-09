using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cricket07.FaceStudio.Core;
using Microsoft.Win32;

namespace Cricket07.FaceStudio.Views;

public partial class TextureWindow : Window
{
    private TextureCatalog? _catalog;
    private byte[]? _replacement;
    private CancellationTokenSource? _operation;
    private string? _cachePath;
    private int _selectionVersion;
    private TextureAsset? Selected => Results.SelectedItem as TextureAsset;

    public TextureWindow() { InitializeComponent(); }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Select Cricket 07 folder", InitialDirectory = RootPath.Text };
        if (picker.ShowDialog(this) != true) return;
        string root = picker.FolderName;
        RootPath.Text = root;
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(s => Status.Text = "Scanning: " + s);
            var catalog = await Task.Run(() => TextureCatalog.Scan(root, progress, _operation!.Token));
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
            _cachePath = Path.Combine(AppContext.BaseDirectory, "workspace", "catalogs", key + ".json");
            if (File.Exists(_cachePath))
            {
                try { catalog.Aliases = JsonSerializer.Deserialize<TextureCatalog>(File.ReadAllText(_cachePath))?.Aliases ?? new(); }
                catch (JsonException) { catalog.Diagnostics.Add("Previous catalogue was invalid; aliases could not be restored."); }
            }
            catalog.Save(_cachePath);
            _catalog = catalog;
            RefreshSearch();
            Status.Text = $"Indexed {catalog.Textures.Count} entries. {catalog.Diagnostics.Count} scan notes. Unknown formats remain read-only.";
        }
        catch (OperationCanceledException) { Status.Text = "Scan cancelled. Previous catalogue retained."; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        if (busy) _operation = new CancellationTokenSource();
        else { _operation?.Dispose(); _operation = null; }
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        Results.IsEnabled = !busy;
        RootPath.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy && Selected?.WriteStatus == "Ready";
        ExportButton.IsEnabled = !busy && _replacement is not null;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) => _operation?.Cancel();
    private void OnSearch(object sender, TextChangedEventArgs e) { if (Results is not null) RefreshSearch(); }
    private void RefreshSearch() => Results.ItemsSource = _catalog?.Search(SearchBox.Text).ToList();

    private async void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        int version = ++_selectionVersion;
        _replacement = null;
        ReplacementPreview.Source = null;
        OriginalPreview.Source = null;
        ExportButton.IsEnabled = false;
        var asset = Selected;
        ImportButton.IsEnabled = asset?.WriteStatus == "Ready" && _operation is null;
        if (asset is null) return;
        AliasBox.Text = _catalog?.Aliases.GetValueOrDefault(asset.Key) ?? "";
        Details.Text = $"{asset.Label}\n{asset.Dimensions} · {asset.Encoding} · {asset.MipCount} mip level(s)\n{asset.WriteStatus}\nFSH SHA-256: {asset.ContentHash}";
        if (asset.WriteStatus != "Ready") return;
        try
        {
            var preview = await Task.Run(() => FshTextureEditor.Preview(TexturePackage.ReadVerified(asset), asset.EntryIndex));
            if (version == _selectionVersion && preview is not null) OriginalPreview.Source = Bitmap(preview.Width, preview.Height, preview.PixelsBgra32);
        }
        catch (Exception ex) { if (version == _selectionVersion) Status.Text = ex.Message; }
    }

    private void OnSaveAlias(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || Selected is null || _cachePath is null) return;
        try { _catalog.Aliases[Selected.Key] = AliasBox.Text.Trim(); _catalog.Save(_cachePath); Status.Text = "Alias saved. Search by this name or its tags."; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var asset = Selected;
        if (asset is null) return;
        var picker = new OpenFileDialog { Filter = "Texture images|*.bmp;*.tga", Title = "Import replacement texture" };
        if (picker.ShowDialog(this) != true) return;
        _replacement = null; ReplacementPreview.Source = null;
        SetBusy(true);
        try
        {
            ImportedImage image;
            if (Path.GetExtension(picker.FileName).Equals(".tga", StringComparison.OrdinalIgnoreCase))
                image = TgaImage.Read(File.ReadAllBytes(picker.FileName));
            else
            {
                using var stream = File.OpenRead(picker.FileName);
                var decoder = new BmpBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096) throw new InvalidDataException("Image dimensions exceed 4096.");
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
                converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
                image = new(converted.PixelWidth, converted.PixelHeight, pixels);
            }
            var bytes = await Task.Run(() => FshTextureEditor.Replace(TexturePackage.ReadVerified(asset), asset.EntryIndex, image.Width, image.Height, image.Bgra));
            _operation!.Token.ThrowIfCancellationRequested();
            var preview = FshTextureEditor.Preview(bytes, asset.EntryIndex);
            _replacement = bytes;
            if (preview is not null) ReplacementPreview.Source = Bitmap(preview.Width, preview.Height, preview.PixelsBgra32);
            Status.Text = "Replacement encoded. Review the compressed preview, then export the package.";
        }
        catch (OperationCanceledException) { Status.Text = "Import cancelled."; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var asset = Selected; var bytes = _replacement;
        if (asset is null || bytes is null || _catalog is null) return;
        var picker = new SaveFileDialog { FileName = Path.GetFileName(asset.SourcePath), Filter = "Game asset|*.*", Title = "Export to a staging folder outside the game" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            string root = _catalog.Root;
            await Task.Run(() => TexturePackage.Export(asset, bytes, picker.FileName, root, _operation!.Token));
            Status.Text = "Exported package and SHA-256 manifest: " + picker.FileName;
        }
        catch (OperationCanceledException) { Status.Text = "Export cancelled."; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
        var report = new Window { Owner = this, Title = "Face Studio scan report", Width = 850, Height = 500,
            Content = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Text = _catalog is null ? "No scan yet." : string.Join(Environment.NewLine, _catalog.Diagnostics.Prepend($"{_catalog.Textures.Count} indexed texture entries. Duplicate locations retained; active loader precedence is not inferred.")) } };
        report.Show();
    }

    private static BitmapSource Bitmap(int w, int h, byte[] pixels)
    {
        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        image.Freeze(); return image;
    }
}

