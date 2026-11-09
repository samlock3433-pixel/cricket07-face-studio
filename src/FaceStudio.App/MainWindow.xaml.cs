using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Cricket07.FaceStudio.Core;
using Cricket07.FaceStudio.Views;
using Microsoft.Win32;

namespace Cricket07.FaceStudio;

public sealed record Template(string Name,string Category,string Model,string? Texture,string Source,string Sha256,bool HasHair=false)
{
    public string? PreviewPath=>Model.Length==0?null:Path.Combine(AppContext.BaseDirectory,"templates","previews",Category+"-"+Sha256[..12]+".png");
}

public partial class MainWindow : Window
{
    private string _converter;
    private readonly string _templates=Path.Combine(AppContext.BaseDirectory,"templates");
    private readonly string _settings=Path.Combine(AppContext.BaseDirectory,"workspace","converter.txt");
    public MainWindow()
    {
        InitializeComponent();
        StudioModel.SweepWorkspaces();
        _converter=Path.Combine(AppContext.BaseDirectory,"tools","otools","otools-release-0179","otools.exe");
        if(File.Exists(_settings)) _converter=File.ReadAllText(_settings).Trim();
        ConverterInfo.Text=File.Exists(_converter)?"Cricket 07 .o converter ready":"Select the OTools executable to enable .o import/export.";
        try
        {
            var templates=JsonSerializer.Deserialize<List<Template>>(File.ReadAllText(Path.Combine(_templates,"catalog.json")))??new();
            Heads.ItemsSource=templates.Where(t=>t.Category=="Head").ToList(); Heads.SelectedIndex=0;
        }
        catch(Exception ex){Status.Text="Template library: "+ex.Message;}
    }
    private string Resolve(Template template)
    {
        string path=ModelDocument.ContainedPath(_templates,template.Model);
        if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))!=template.Sha256) throw new IOException("Template fingerprint changed. Import the changed .o as your own model instead.");
        return path;
    }
    private async void OnStart(object sender,RoutedEventArgs e)
    {
        if(Heads.SelectedItem is not Template head)return;
        try { await OpenAsync(Resolve(head),null); }
        catch(Exception ex){Status.Text=ex.Message;}
    }
    private async void OnOpen(object sender,RoutedEventArgs e)
    {
        var picker=new OpenFileDialog{Title="Open Cricket 07 face",Filter="Cricket 07 face/model|*.big;*.o|Face package|*.big|Model|*.o"};
        if(picker.ShowDialog(this)!=true)return;
        try{await OpenAsync(picker.FileName,null);}catch(Exception ex){Status.Text=ex.Message;}
    }
    private async Task OpenAsync(string head,string? hair)
    {
        StartButton.IsEnabled=OpenButton.IsEnabled=false;
        Status.Text="Opening .o model and preparing editable mesh parts…";
        try
        {
            var converter=new OtoolsConverter(_converter);
            var model=await StudioModel.OpenAsync(head,converter);
            var hairModel=hair is null?null:await StudioModel.OpenAsync(hair,converter);
            new FaceSculptWindow(model,hairModel){Owner=this}.Show();
            Status.Text="Editor opened. Export .o bundle when your edits are ready.";
        }
        finally{StartButton.IsEnabled=OpenButton.IsEnabled=true;}
    }
    private void OnTextures(object sender,RoutedEventArgs e)=>new TextureWindow{Owner=this}.Show();
    private void OnChooseConverter(object sender,RoutedEventArgs e)
    {
        var picker=new OpenFileDialog{Filter="OTools executable|otools.exe",Title="Select OTools"};
        if(picker.ShowDialog(this)!=true)return;
        try{Directory.CreateDirectory(Path.GetDirectoryName(_settings)!);File.WriteAllText(_settings,picker.FileName);_converter=picker.FileName;ConverterInfo.Text="OTools: "+_converter;}
        catch(Exception ex){Status.Text=ex.Message;}
    }
}
