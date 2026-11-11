using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Cricket07.FaceStudio.Core;
using Microsoft.Win32;

namespace Cricket07.FaceStudio.Views;

public partial class FaceSculptWindow : Window
{
    private readonly StudioModel[] _models;
    private readonly List<(StudioModel Model,int Index,GeometryModel3D Visual)> _parts=new();
    private readonly List<Material> _originalMaterials=new();
    private readonly Stack<UndoStep> _undo=new(),_redo=new();
    private readonly Rect3D _bounds;
    private readonly double _size;
    private Vector3? _center;
    private bool _dirty,_exporting;
    private int SelectedPart => Math.Max(0,PartPicker.SelectedIndex);
    private MeshGeometry3D Mesh => (MeshGeometry3D)_parts[SelectedPart].Visual.Geometry;
    private float? Plane => Symmetry.IsChecked==true?(float)(_bounds.X+_bounds.SizeX/2):null;
    private float? Floor => LockNeck.IsChecked==true?(float)(_bounds.Y+_bounds.SizeY*0.1):null;

    public FaceSculptWindow(StudioModel head,StudioModel? hair=null)
    {
        InitializeComponent();
        _models=hair is null?new[]{head}:new[]{head,hair};
        var group=new Model3DGroup();
        for(int m=0;m<_models.Length;m++)
        for(int p=0;p<_models[m].Geometry.Count;p++)
        {
            var visual=_models[m].Geometry[p];
            _parts.Add((_models[m],p,visual));_originalMaterials.Add(visual.Material);group.Children.Add(visual);
            var docPart=_models[m].Document.Parts[p];
            string texture=Path.GetFileNameWithoutExtension(docPart.ImageUri)??"";
            string label=(string.Equals(texture,"hair",StringComparison.OrdinalIgnoreCase)?"Hair":docPart.Positions.Length<64?"Small piece":"Head")
                +$" · {(texture.Length>0?texture:"no")} texture · {docPart.Positions.Length} points"+(m==0?"":" (hair model)");
            var lockBox=new CheckBox{Content="Lock",Margin=new Thickness(0,0,10,0),VerticalAlignment=VerticalAlignment.Center,ToolTip="Keep this part's shape: no sculpt tool moves it."};
            lockBox.Click+=(_,_)=>Status.Text=LockSummary();
            _locks.Add(lockBox);
            var row=new StackPanel{Orientation=Orientation.Horizontal,ToolTip=docPart.Name};
            row.Children.Add(lockBox);row.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center});
            PartPicker.Items.Add(row);
        }
        _bounds=group.Bounds;_size=Math.Max(_bounds.SizeX,Math.Max(_bounds.SizeY,_bounds.SizeZ));
        if(!double.IsFinite(_size)||_size<=0)throw new InvalidDataException("Empty model bounds.");
        Viewport.Children.Add(new ModelVisual3D{Content=group});
        PartPicker.SelectedIndex=0;        Viewport.Children.Add(_referencePlane);_referenceReady=true;
        InitPaint();
        Loaded+=(_,_)=>SetView("front");
    }

    private void OnPartChanged(object sender,SelectionChangedEventArgs e){_center=null;}

    // ---- part locks (like a locked layer: the part keeps its shape) ----
    private readonly List<CheckBox> _locks=new();
    public bool Locked(int part)=>part<_locks.Count&&_locks[part].IsChecked==true;
    public void SetLocked(int part,bool locked){_locks[part].IsChecked=locked;Status.Text=LockSummary();}
    private string LockSummary()
    {
        int locked=Enumerable.Range(0,_parts.Count).Count(Locked);
        return locked==0?"No parts locked: sculpt tools move every part near the brush."
            :locked==_parts.Count?"Every part is locked: sculpt tools change nothing until you unlock one."
            :$"{locked} of {_parts.Count} parts locked: they keep their shape while you sculpt.";
    }
    private void OnLockOthers(object sender,RoutedEventArgs e){for(int p=0;p<_parts.Count;p++)_locks[p].IsChecked=p!=SelectedPart;Status.Text=LockSummary();}
    private void OnUnlockAll(object sender,RoutedEventArgs e){foreach(var box in _locks)box.IsChecked=false;Status.Text=LockSummary();}
    private void OnPick(object sender,MouseButtonEventArgs e)
    {
        if(_parts.Count==0||_exporting)return;
        if(HitHead(e.GetPosition(Viewport.Viewport)) is not { } closest)return;
        PartPicker.SelectedIndex=_parts.FindIndex(p=>p.Visual.Geometry==closest.MeshHit);
        var p=closest.PointHit;_center=new Vector3((float)p.X,(float)p.Y,(float)p.Z);
        Status.Text=$"Feature selected at {p.X:F2}, {p.Y:F2}, {p.Z:F2}. Move or smooth this region.";e.Handled=true;
    }
    private Vector3[] Positions(int part)=>((MeshGeometry3D)_parts[part].Visual.Geometry).Positions.Select(p=>new Vector3((float)p.X,(float)p.Y,(float)p.Z)).ToArray();
    private Vector3[] Positions()=>Positions(SelectedPart);
    private Vector3[][] AllPositions()=>Enumerable.Range(0,_parts.Count).Select(Positions).ToArray();
    private Point3D[][] Snapshot()=>_parts.Select(p=>((MeshGeometry3D)p.Visual.Geometry).Positions.ToArray()).ToArray();
    private void OnMove(object sender,RoutedEventArgs e)
    {
        if(_center is not Vector3 center){Status.Text="Click a feature on the selected mesh part first.";return;}
        var v=((string)((Button)sender).Tag).Split(',').Select(s=>float.Parse(s,CultureInfo.InvariantCulture)).ToArray();
        // Every part moves with the same falloff, so eyeballs, teeth and hair follow the eyelids, lips and scalp.
        Edit(p=>SculptBrush.Move(Positions(p),center,new Vector3(v[0],v[1],v[2])*(float)(_size*Strength.Value/100),(float)(_size*Radius.Value/100),Plane,Floor));
    }
    private void OnSmooth(object sender,RoutedEventArgs e)
    {
        if(_center is not Vector3 center){Status.Text="Click a feature on the selected mesh part first.";return;}
        // Smoothing is surface-local: smoothing eyeballs or teeth would shrink them.
        Edit(p=>p!=SelectedPart?null:SculptBrush.Smooth(Positions(p),Mesh.TriangleIndices.ToArray(),center,(float)(_size*Radius.Value/100),0.2f,Plane,Floor));
    }
    private void Edit(Func<int,Vector3[]?> brush)
    {
        var before=Snapshot();
        if(Enumerable.Range(0,_parts.Count).All(Locked)){Status.Text=LockSummary();return;}
        for(int p=0;p<_parts.Count;p++)if(!Locked(p)&&brush(p) is { } positions)SetPositions(p,positions.Select(v=>new Point3D(v.X,v.Y,v.Z)));
        _undo.Push(new UndoStep(before));_redo.Clear();
        Status.Text="Sculpt updated."+(Enumerable.Range(0,_parts.Count).Any(Locked)?" Locked parts kept their shape.":" Topology, UVs and skin data remain unchanged.");
    }
    private void SetPositions(int part,IEnumerable<Point3D> positions)
    {
        var mesh=(MeshGeometry3D)_parts[part].Visual.Geometry;var next=positions.ToArray();
        if(mesh.Positions.SequenceEqual(next))return; // keep the authored preview normals on untouched parts
        mesh.Positions=new Point3DCollection(next);mesh.Normals=new Vector3DCollection();_dirty=true;
        QueueRedrawPins();QueueWireframe();
    }
    private void Restore(Point3D[][] snapshot){for(int p=0;p<_parts.Count;p++)SetPositions(p,snapshot[p]);}
    private void OnUndo(object sender,RoutedEventArgs e)
    {
        if(!_undo.TryPop(out var previous))return;
        _redo.Push(Current(previous));Apply(previous);
    }
    private void OnRedo(object sender,RoutedEventArgs e)
    {
        if(!_redo.TryPop(out var next))return;
        _undo.Push(Current(next));Apply(next);
    }
    private void OnLikeness(object sender,RoutedEventArgs e)
    {
        try
        {
            var parts=_parts.Select(p=>p.Model.Document.Parts[p.Index]).ToList();
            // Landmarks live on the head's face-atlas parts; the fit itself moves every part (eyes, teeth, hair).
            var face=Enumerable.Range(0,_parts.Count).Where(i=>_parts[i].Model==_models[0]&&string.Equals(Path.GetFileNameWithoutExtension(parts[i].ImageUri),"face",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(face.Length==0)throw new InvalidDataException("This head has no face texture to locate landmarks on.");
            float neck=(float)(_bounds.Y+_bounds.SizeY*0.1); // the neck joins the body: always held
            var baseTexture=(_parts[face[0]].Visual.Material as DiffuseMaterial)?.Brush is ImageBrush { ImageSource:BitmapSource image }?image:null;
            var window=new LikenessWindow(_parts.Select(p=>p.Visual).ToList(),parts,face,AllPositions(),neck,
                baseTexture,Enumerable.Range(0,_parts.Count).Select(Locked).ToArray()){Owner=this};
            if(window.ShowDialog()!=true||window.Result is not { } result)return;
            ApplyLikeness(result,window.TextureResult);
            Status.Text=window.TextureResult is null?"Head shape fitted to the photos. Undo restores the previous shape.":"Photo shape and texture applied. Undo restores both. Save as .big exports the face.";
        }
        catch(Exception ex){Status.Text=ex.Message;}
    }
    internal void ApplyLikeness(Vector3[][] result,BitmapSource? texture)
    {
        var before=Snapshot();
        int face=_models[0].Document.Parts.FindIndex(p=>string.Equals(Path.GetFileNameWithoutExtension(p.ImageUri),"face",StringComparison.OrdinalIgnoreCase));
        var model=_models[0];
        var step=texture is null?new UndoStep(before):new UndoStep(before,model,face,model.TextureOverrides.GetValueOrDefault(model.Document.Parts[face].ImageUri!),PinsOf(model,face));
        if(texture is not null){model.Wrap(face,texture);if(PinsOf(model,face) is not null)_pins=null;}
        for(int p=0;p<_parts.Count;p++)if(!Locked(p))SetPositions(p,result[p].Select(v=>new Point3D(v.X,v.Y,v.Z)));
        _undo.Push(step);_redo.Clear();_dirty=true;QueueRedrawPins();
    }
    /// <summary>Index into _parts of the head's hair mesh (the part textured with "hair"), or -1.</summary>
    private int HairPart=>_parts.FindIndex(p=>string.Equals(Path.GetFileNameWithoutExtension(p.Model.Document.Parts[p.Index].ImageUri),"hair",StringComparison.OrdinalIgnoreCase));
    private const string NoHair="This head has no hair mesh: its hair is painted on the face texture. Start from a head marked 'with hair' (or open a face .big that has one) to shape 3D hair.";
    private void OnSelectHair(object sender,RoutedEventArgs e)
    {
        int hair=HairPart;if(hair<0){Status.Text=NoHair;return;}
        PartPicker.SelectedIndex=hair;Status.Text="Hair selected. Volume +/- changes its fullness; grab (left drag) changes its outline and length; Smooth region evens it out.";
    }
    private void OnHairVolume(object sender,RoutedEventArgs e)
    {
        int hair=HairPart;if(hair<0){Status.Text=NoHair;return;}
        bool whole=WholeHair.IsChecked==true;
        if(!whole&&_center is null){Status.Text="Click the spot on the hair to puff out or flatten, or tick 'Whole hair'.";return;}
        float amount=float.Parse((string)((Button)sender).Tag,System.Globalization.CultureInfo.InvariantCulture)*(float)(_size*Strength.Value/100);
        var triangles=((MeshGeometry3D)_parts[hair].Visual.Geometry).TriangleIndices.ToArray();
        Edit(p=>p!=hair?null:SculptBrush.Inflate(Positions(p),triangles,_center??default,amount,(float)(_size*Radius.Value/100),Plane,Floor,whole));
        Status.Text=(amount>0?"Hair fuller":"Hair flatter")+(whole?" (whole hair).":" around the clicked spot.")+" Undo restores it.";
    }
    /// <summary>
    /// Gives this head hair from a template: the template head takes this head's shape and face texture (via the shared
    /// face-atlas layout) and keeps its hair, which follows the new skull. Opens the result in a new editor.
    /// </summary>
    private async void OnAddHair(object sender,RoutedEventArgs e)
    {
        try
        {
            var picker=new HairTemplateWindow{Owner=this};
            if(picker.ShowDialog()!=true||picker.Result is not { } templatePath)return;
            IsEnabled=false;Status.Text="Giving the template this head's shape and face texture…";
            var withHair=await AddHairAsync(templatePath);
            new FaceSculptWindow(withHair){Owner=Owner,Title=$"Face Studio — {withHair.SaveName} with hair from stock head {Path.GetFileName(Path.GetDirectoryName(templatePath))}"}.Show();
            Status.Text="Opened the head with hair in a new editor. Shape and colour the hair there, then Save as .big. This head is unchanged.";
        }
        catch(Exception ex){Status.Text=ex.Message;}
        finally{IsEnabled=true;}
    }
    /// <summary>Opens the template and transfers this head's current shape and face texture onto it.</summary>
    internal async Task<StudioModel> AddHairAsync(string templatePath)
    {
        var source=_models[0];
        int[] Face(ModelDocument d)=>d.Parts.Select((p,i)=>(p,i)).Where(x=>string.Equals(Path.GetFileNameWithoutExtension(x.p.ImageUri),"face",StringComparison.OrdinalIgnoreCase)).Select(x=>x.i).ToArray();
        var sourceFace=Face(source.Document);
        if(sourceFace.Length==0)throw new InvalidDataException("This head has no face texture to match the template with.");
        var sourcePositions=Enumerable.Range(0,source.Geometry.Count).Select(p=>((MeshGeometry3D)source.Geometry[p].Geometry).Positions.Select(v=>new Vector3((float)v.X,(float)v.Y,(float)v.Z)).ToArray()).ToArray();
        var template=await StudioModel.OpenAsync(templatePath,source.Converter);
        var targetPositions=template.Document.Parts.Select(p=>p.Positions.ToArray()).ToArray();
        var all=targetPositions.SelectMany(p=>p).ToArray();float low=all.Min(v=>v.Y),height=all.Max(v=>v.Y)-low;
        // The neck keeps the template's shape so it still joins the body; the transfer fades in just above it.
        var result=ShapeTransfer.Transfer(source.Document.Parts,sourcePositions,sourceFace,template.Document.Parts,targetPositions,Face(template.Document),low+height*0.1f,height*0.08f);
        for(int p=0;p<template.Geometry.Count;p++)
        {
            var mesh=(MeshGeometry3D)template.Geometry[p].Geometry;
            mesh.Positions=new Point3DCollection(result.Positions[p].Select(v=>new Point3D(v.X,v.Y,v.Z)));mesh.Normals=new Vector3DCollection();
        }
        var templateFace=Face(template.Document);
        if(templateFace.Length>0)template.Wrap(templateFace[0],source.CurrentTexture(sourceFace[0])); // the player's face texture, at its own size
        string name=Path.GetFileNameWithoutExtension(source.SourcePath);
        template.SaveName=source.SaveName??(name=="model"?Path.GetFileName(Path.GetDirectoryName(source.SourcePath)):name);
        return template;
    }
    internal static BitmapSource? PickImage(Window owner)
    {
        var picker=new OpenFileDialog{Filter="Images|*.png;*.jpg;*.jpeg;*.bmp;*.tga"};
        if(picker.ShowDialog(owner)!=true)return null;
        if(new FileInfo(picker.FileName).Length>64*1024*1024)throw new InvalidDataException("Image exceeds 64 MiB.");
        if(Path.GetExtension(picker.FileName).Equals(".tga",StringComparison.OrdinalIgnoreCase))
        {
            var tga=TgaImage.Read(File.ReadAllBytes(picker.FileName));
            return BitmapSource.Create(tga.Width,tga.Height,96,96,PixelFormats.Bgra32,null,tga.Bgra,tga.Width*4);
        }
        using var input=File.OpenRead(picker.FileName);
        var bitmap=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
        CheckImage(bitmap);return bitmap;
    }
    private static void CheckImage(BitmapSource image)
    {if(image.PixelWidth>4096||image.PixelHeight>4096)throw new InvalidDataException("Image dimensions exceed 4096.");}
    private void OnReference(object sender,RoutedEventArgs e)
    {
        try{var image=PickImage(this);if(image is null)return;bool front=(string)((Button)sender).Tag=="front";if(front)FrontReference.Source=image;else SideReference.Source=image;ReferenceMode.SelectedIndex=front?1:2;SetView(front?"front":"side");UpdateReference();_dirty=true;}
        catch(Exception ex){Status.Text=ex.Message;}
    }
    private void OnTexture(object sender,RoutedEventArgs e)
    {
        try{var image=PickImage(this);if(image is null)return;var part=_parts[SelectedPart];part.Model.ValidateTexture(part.Index,image);RecordTexture(part.Model,part.Index);part.Model.Wrap(part.Index,image);_dirty=true;Status.Text="Texture wrapped on matching material parts. Export preserves the original FSH encoding and requires matching image dimensions.";}
        catch(Exception ex){Status.Text=ex.Message;}
    }
    private static byte[]? Png(ImageSource? image)
    {
        if(image is not BitmapSource bitmap)return null;
        using var memory=new MemoryStream();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(memory);return memory.ToArray();
    }
    private static BitmapSource? FromPng(byte[]? data)
    {
        if(data is null)return null;
        using var input=new MemoryStream(data);var image=new PngBitmapDecoder(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];CheckImage(image);return image;
    }
    public sealed record SculptProject(int Version,string[] SourceHashes,double[][][] Positions,Dictionary<string,byte[]>[] Textures,byte[]? Front,byte[]? Side)
    {
        public double[][]? ReferenceSettings {get;init;}
        public int ReferenceView {get;init;}
    }

    public void SaveProject(string path)
    {
        var project=new SculptProject(2,_models.Select(m=>m.SourceHash).ToArray(),
            _parts.Select(p=>((MeshGeometry3D)p.Visual.Geometry).Positions.Select(v=>new[]{v.X,v.Y,v.Z}).ToArray()).ToArray(),
            _models.Select(m=>m.TextureOverrides).ToArray(),Png(FrontReference.Source),Png(SideReference.Source)){ReferenceSettings=_referenceSettings,ReferenceView=ReferenceMode.SelectedIndex};
        string json=JsonSerializer.Serialize(project);
        if(System.Text.Encoding.UTF8.GetByteCount(json)>128*1024*1024)throw new InvalidDataException("Project exceeds the 128 MiB supported size. Reduce reference image size.");
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,json);File.Move(temp,path,true);_dirty=false;}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private void OnSave(object sender,RoutedEventArgs e)
    {
        var picker=new SaveFileDialog{Filter="Face Studio project|*.c07face.json",FileName="face.c07face.json"};if(picker.ShowDialog(this)!=true)return;
        try{SaveProject(picker.FileName);Status.Text="Project saved, including all head/hair mesh edits and embedded references.";}catch(Exception ex){Status.Text=ex.Message;}
    }
    public void LoadProject(string path)
    {
        if(new FileInfo(path).Length>128*1024*1024)throw new InvalidDataException("Project exceeds 128 MiB.");
        var project=JsonSerializer.Deserialize<SculptProject>(File.ReadAllText(path))??throw new InvalidDataException("Invalid project.");
        if(project.Version!=2||!project.SourceHashes.SequenceEqual(_models.Select(m=>m.SourceHash))||project.Positions.Length!=_parts.Count||project.Textures.Length!=_models.Length)
            throw new InvalidDataException("Open the same head and hair templates before loading this project.");
        for(int p=0;p<_parts.Count;p++)
        {
            var vertices=project.Positions[p];
            if(vertices.Length!=((MeshGeometry3D)_parts[p].Visual.Geometry).Positions.Count||vertices.Any(v=>v is null||v.Length!=3||v.Any(n=>!double.IsFinite(n)||Math.Abs(n)>1e6)))
                throw new InvalidDataException("Invalid project vertex layout.");
        }
        var textures=new List<(int Model,int Part,BitmapSource Image)>();
        for(int m=0;m<_models.Length;m++)foreach(var pair in project.Textures[m])
        {
            int part=_models[m].Document.Parts.FindIndex(p=>p.ImageUri==pair.Key);
            if(part<0)throw new InvalidDataException("Unknown project texture binding.");
            textures.Add((m,part,FromPng(pair.Value)!));
        }
        var front=FromPng(project.Front);var side=FromPng(project.Side);
        if(project.ReferenceView is <0 or >2 || project.ReferenceSettings is { } settings && (settings.Length!=2||settings.Any(s=>s is null||s.Length is not (5 or 9)||s.Any(v=>!double.IsFinite(v))||(s.Length==9&&(s[5]<0||s[6]<0||s[7]>1||s[8]>1||s[7]-s[5]<0.005||s[8]-s[6]<0.005))||s[0]<0||s[0]>1||s[1]<0.2||s[1]>3||Math.Abs(s[2])>1||Math.Abs(s[3])>1||s[4] is not (0 or 1))))throw new InvalidDataException("Invalid reference alignment settings.");
        foreach(var texture in textures)_models[texture.Model].ValidateTexture(texture.Part,texture.Image);
        for(int p=0;p<_parts.Count;p++)
        {
            SetPositions(p,project.Positions[p].Select(v=>new Point3D(v[0],v[1],v[2])));
            _parts[p].Visual.Material=_originalMaterials[p];_parts[p].Visual.BackMaterial=StudioModel.InteriorMaterial;
        }
        foreach(var model in _models)model.TextureOverrides.Clear();
        foreach(var texture in textures)_models[texture.Model].Wrap(texture.Part,texture.Image);
        FrontReference.Source=front;SideReference.Source=side;
        // Projects saved before cropping existed have 5 values per reference: add "whole photo".
        _referenceSettings=project.ReferenceSettings?.Select(s=>s.Length==9?s:s.Concat(new[]{0d,0d,1d,1d}).ToArray()).ToArray()??new[]{DefaultReference(),DefaultReference()};
        ReferenceMode.SelectedIndex=project.ReferenceView;OnReferenceMode(ReferenceMode,null);
        _undo.Clear();_redo.Clear();_center=null;_dirty=false;
    }
    private void OnLoad(object sender,RoutedEventArgs e)
    {
        if(_dirty&&MessageBox.Show(this,"Discard unsaved edits and load another project?","Load project",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        var picker=new OpenFileDialog{Filter="Face Studio project|*.c07face.json"};if(picker.ShowDialog(this)!=true)return;
        try{LoadProject(picker.FileName);Status.Text="All mesh parts, textures, and references restored.";}catch(Exception ex){Status.Text=ex.Message;}
    }
    private async void OnExport(object sender,RoutedEventArgs e)
    {
        string head=_models[0].SourcePath,name=_models[0].SaveName??Path.GetFileNameWithoutExtension(head);
        if(name=="model")name=Path.GetFileName(Path.GetDirectoryName(head))??"face"; // templates live in heads\<id>\model.o
        var picker=new SaveFileDialog{Title="Save face as .big",Filter="Face package|*.big",FileName=name+".big"};
        if(picker.ShowDialog(this)!=true)return;
        // The checks run in a scratch folder; only the .big is written where the user chose.
        string scratch=Path.Combine(StudioModel.WorkspaceRoot,"export-"+Guid.NewGuid().ToString("N"));
        string temp=picker.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";
        _exporting=true;IsEnabled=false;
        try
        {
            Status.Text="Building the face and reopening it for geometry, UV and skin-data checks…";
            await _models[0].ExportAsync(scratch);
            File.WriteAllBytes(temp,StudioModel.BuildFacePackage(scratch));
            File.Move(temp,picker.FileName,true);
            Status.Text="Saved "+picker.FileName+"."+(_models.Length>1?" The extra hair template is not inside it: a face .big holds one model.":"");
        }
        catch(Exception ex){Status.Text=ex.Message;}
        finally
        {
            IsEnabled=true;_exporting=false;
            try{if(File.Exists(temp))File.Delete(temp);if(Directory.Exists(scratch))Directory.Delete(scratch,true);}catch(IOException){}
        }
    }
    private void OnClosed(object? sender,EventArgs e){foreach(var model in _models)model.DeleteWorkspace();}
    private void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if(_exporting){e.Cancel=true;Status.Text="Wait for the active export to complete.";return;}
        if(_dirty&&MessageBox.Show(this,"Close without saving this face project?","Unsaved edits",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)e.Cancel=true;
    }
}

