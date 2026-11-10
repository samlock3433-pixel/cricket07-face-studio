using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cricket07.FaceStudio;
using Cricket07.FaceStudio.Core;
using Cricket07.FaceStudio.Views;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async (_,_)=>{
            try
            {
                string studio=args.Length>0?Path.GetFullPath(args[0]):Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
                string templates=Path.Combine(studio,"templates");
                string executable=Path.Combine(studio,"vendor","otools","otools-release-0179","otools.exe");
                var converter=new OtoolsConverter(executable);
                string output=Path.Combine(studio,"work","verification-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
                if(args.Length>3&&args[1]=="likeness")
                {
                    // Trial on real pictures: likeness <front photo> <face texture> [stock head id]. Saves before/after renders.
                    BitmapSource Load(string path){using var s=File.OpenRead(path);var frame=BitmapDecoder.Create(s,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];var c=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);c.Freeze();return c;}
                    var head=await StudioModel.OpenAsync(ModelDocument.ContainedPath(templates,$"heads/{(args.Length>4?args[4]:"1188")}/model.o"),converter);
                    int face=head.Document.Parts.FindIndex(p=>Path.GetFileNameWithoutExtension(p.ImageUri)=="face");
                    head.Wrap(face,Load(args[3]));
                    var positions=head.Document.Parts.Select(p=>p.Positions.ToArray()).ToArray();
                    var photo=Load(args[2]);
                    var before=new[]{FrontRender(head),FrontRender(head,-90)};
                    var faceParts=head.Document.Parts.Select((p,i)=>(p,i)).Where(x=>Path.GetFileNameWithoutExtension(x.p.ImageUri)=="face").Select(x=>x.i).ToArray();
                    var window=new LikenessWindow(head.Geometry,head.Document.Parts,faceParts,positions,positions.SelectMany(p=>p).Min(v=>v.Y)+2);Layout(window,1320,900);
                    LikenessWindow.ModelPath=Path.Combine(studio,"vendor","facemesh","face_landmarks.onnx");
                    window.LoadPhoto(false,photo);
                    if(!window.AutoPlaceFront())throw new Exception("No face found in the front photo.");
                    BitmapSource? sidePhoto=args.Length>5?Load(args[5]):null;
                    if(sidePhoto is not null){window.LoadPhoto(true,sidePhoto);Console.WriteLine("side photo face found: "+window.AutoPlaceSide());}
                    var fitted=window.Fit();
                    Console.WriteLine($"largest move: {positions.Zip(fitted).SelectMany(z=>z.First.Zip(z.Second,Vector3.Distance)).Max():F2} units");
                    for(int p=0;p<fitted.Length;p++){var m=(MeshGeometry3D)head.Geometry[p].Geometry;m.Positions=new Point3DCollection(fitted[p].Select(v=>new Point3D(v.X,v.Y,v.Z)));m.Normals=new Vector3DCollection();}
                    var after=new[]{FrontRender(head),FrontRender(head,-90)};
                    var visual=new DrawingVisual();
                    using(var dc=visual.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.Black,null,new Rect(0,0,600*3,700*2));
                        double ph=700,pw=photo.PixelWidth*ph/photo.PixelHeight;
                        dc.DrawImage(photo,new Rect(1200+(600-pw)/2,0,pw,ph));
                        if(sidePhoto is not null){double sw=sidePhoto.PixelWidth*ph/sidePhoto.PixelHeight;dc.DrawImage(sidePhoto,new Rect(1200+(600-sw)/2,700,sw,ph));foreach(FrameworkElement m in ((Canvas)window.FindName("SidePhotoCanvas")).Children)dc.DrawEllipse(null,new Pen(Brushes.Orange,2),new Point(1200+(600-sw)/2+(Canvas.GetLeft(m)+m.Width/2)*ph/sidePhoto.PixelHeight,700+(Canvas.GetTop(m)+m.Height/2)*ph/sidePhoto.PixelHeight),5,5);}
                        dc.DrawImage(before[0],new Rect(0,0,600,700));dc.DrawImage(after[0],new Rect(600,0,600,700));
                        dc.DrawImage(before[1],new Rect(0,700,600,700));dc.DrawImage(after[1],new Rect(600,700,600,700));
                        var type=new Typeface("Segoe UI");void Label(string t,double x,double y)=>dc.DrawText(new FormattedText(t,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,type,26,Brushes.Yellow,1),new Point(x+12,y+8));
                        Label("Before (stock shape)",0,0);Label("After automatic fit",600,0);Label("Front photo",1200,0);
                    }
                    var sheet=new RenderTargetBitmap(1800,1400,96,96,PixelFormats.Pbgra32);sheet.Render(visual);
                    string outFile=Path.Combine(output,"likeness-trial.png");SavePng(sheet,outFile);
                    await head.ExportAsync(Path.Combine(output,"fitted"));
                    File.WriteAllBytes(Path.Combine(output,"likeness-trial.big"),StudioModel.BuildFacePackage(Path.Combine(output,"fitted")));
                    Console.WriteLine("SAVED "+outFile);
                    app.Shutdown(0);return;
                }
                if(args.Length>1)
                {
                    var custom=await StudioModel.OpenAsync(args[1],converter);
                    string bundle=Path.Combine(output,"custom-face");
                    await custom.ExportAsync(bundle);
                    if(!File.ReadAllBytes(args[1]).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(bundle,Path.GetFileName(args[1])))))throw new Exception("No-edit face package changed.");
                    SavePreview(custom,Path.Combine(output,"custom-face.png"));
                    var workspace=new FaceSculptWindow(custom);Layout(workspace,1280,840);
                    var privateFlags=BindingFlags.Instance|BindingFlags.NonPublic;
                    Invoke(workspace,"OnView",new Button{Tag="front"});
                    var viewport=(HelixToolkit.Wpf.HelixViewport3D)workspace.FindName("Viewport");
                    var initialTarget=viewport.Camera.Position+viewport.Camera.LookDirection;
                    typeof(FaceSculptWindow).GetMethod("Navigate",privateFlags)!.Invoke(workspace,new object[]{new System.Windows.Vector(40,20),false});
                    if((viewport.Camera.Position+viewport.Camera.LookDirection-initialTarget).Length>0.0001||viewport.Camera.UpDirection!=new Vector3D(0,1,0))throw new Exception("Orbit lost its target or upright axis.");
                    var initialPosition=viewport.Camera.Position;
                    typeof(FaceSculptWindow).GetMethod("Navigate",privateFlags)!.Invoke(workspace,new object[]{new System.Windows.Vector(20,10),true});
                    if(viewport.Camera.Position==initialPosition)throw new Exception("Pan did not move the camera.");
                    var centerPoint=custom.Document.Parts[0].Positions[0];
                    typeof(FaceSculptWindow).GetField("_center",privateFlags)!.SetValue(workspace,centerPoint);
                    var width=(Slider)workspace.FindName("FeatureWidth");width.Value=1.4;
                    var before=((MeshGeometry3D)custom.Geometry[0].Geometry).Positions.ToArray();
                    Invoke(workspace,"OnProportion",workspace);
                    if(before.SequenceEqual(((MeshGeometry3D)custom.Geometry[0].Geometry).Positions))throw new Exception("Proportion tool did not change geometry.");
                    Invoke(workspace,"OnUndo",workspace);
                    if(!before.SequenceEqual(((MeshGeometry3D)custom.Geometry[0].Geometry).Positions))throw new Exception("Proportion undo failed.");
                    var reference=(Image)workspace.FindName("FrontReference");reference.Source=new BitmapImage(new Uri(Path.Combine(output,"custom-face.png")));
                    ((ComboBox)workspace.FindName("ReferenceMode")).SelectedIndex=1;
                    // Drag the photo with "Move reference only": the photo moves, the head does not, and the position persists.
                    var settingsField=typeof(FaceSculptWindow).GetField("_referenceSettings",privateFlags)!;
                    double[] Offset()=>((double[][])settingsField.GetValue(workspace)!)[0];
                    var headBefore=((MeshGeometry3D)custom.Geometry[0].Geometry).Positions.ToArray();
                    ((CheckBox)workspace.FindName("ReferenceEdit")).IsChecked=true;
                    typeof(FaceSculptWindow).GetMethod("MoveReference",privateFlags)!.Invoke(workspace,new object[]{new System.Windows.Vector(40,-25)});
                    double movedX=Offset()[2],movedY=Offset()[3];
                    if(!(movedX>0&&movedY>0))throw new Exception("Dragging right and up did not move the reference right and up.");
                    if(!headBefore.SequenceEqual(((MeshGeometry3D)custom.Geometry[0].Geometry).Positions))throw new Exception("Moving the reference changed the head.");
                    ((CheckBox)workspace.FindName("ReferenceEdit")).IsChecked=false;
                    string referenceProject=Path.Combine(output,"reference.c07face.json");workspace.SaveProject(referenceProject);
                    Offset()[2]=0;workspace.LoadProject(referenceProject);
                    if(Math.Abs(Offset()[2]-movedX)>0.001||Math.Abs(Offset()[3]-movedY)>0.001)throw new Exception("Reference alignment did not persist.");
                    var plane=(ModelVisual3D)typeof(FaceSculptWindow).GetField("_referencePlane",privateFlags)!.GetValue(workspace)!;
                    if(plane.Content is null)throw new Exception("Reference plane missing.");
                    {
                        // Non-destructive crop: keep the centre quarter. The plane shows exactly that part, with its proportions,
                        // centred where it sat in the whole photo; the crop persists; resetting shows the whole photo again.
                        MeshGeometry3D PlaneMesh()=>(MeshGeometry3D)((GeometryModel3D)plane.Content!).Geometry;
                        var whole=PlaneMesh();var wholeCentre=new Point3D(whole.Positions.Average(p=>p.X),whole.Positions.Average(p=>p.Y),whole.Positions.Average(p=>p.Z));
                        double wholeWidth=whole.Positions.Max(p=>p.X)-whole.Positions.Min(p=>p.X),wholeHeight=whole.Positions.Max(p=>p.Y)-whole.Positions.Min(p=>p.Y);
                        var update=typeof(FaceSculptWindow).GetMethod("UpdateReference",privateFlags)!;
                        (Offset()[5],Offset()[6],Offset()[7],Offset()[8])=(0.25,0.25,0.75,0.75);update.Invoke(workspace,null);
                        var cropped=PlaneMesh();
                        if(!cropped.TextureCoordinates.SequenceEqual(new PointCollection{new(0.25,0.75),new(0.75,0.75),new(0.75,0.25),new(0.25,0.25)}))throw new Exception("Cropped reference does not show the kept area.");
                        double croppedWidth=cropped.Positions.Max(p=>p.X)-cropped.Positions.Min(p=>p.X),croppedHeight=cropped.Positions.Max(p=>p.Y)-cropped.Positions.Min(p=>p.Y);
                        if(Math.Abs(croppedWidth-wholeWidth/2)>1e-6||Math.Abs(croppedHeight-wholeHeight/2)>1e-6)throw new Exception("Cropped reference has the wrong size.");
                        var croppedCentre=new Point3D(cropped.Positions.Average(p=>p.X),cropped.Positions.Average(p=>p.Y),cropped.Positions.Average(p=>p.Z));
                        if((croppedCentre-wholeCentre).Length>1e-6)throw new Exception("Cropping moved the photo.");
                        workspace.SaveProject(referenceProject);(Offset()[5],Offset()[7])=(0,1);workspace.LoadProject(referenceProject);
                        if(Math.Abs(Offset()[5]-0.25)>1e-9||Math.Abs(Offset()[8]-0.75)>1e-9)throw new Exception("Reference crop did not persist.");
                        (Offset()[5],Offset()[6],Offset()[7],Offset()[8])=(0,0,1,1);update.Invoke(workspace,null);
                        if(!PlaneMesh().TextureCoordinates.SequenceEqual(new PointCollection{new(0,1),new(1,1),new(1,0),new(0,0)}))throw new Exception("Reset did not show the whole photo.");
                        // The crop window itself: constructs, keeps a rectangle, and clamps a too-large one to the photo.
                        var cropWindow=new CropWindow(reference.Source as BitmapSource??throw new Exception("No reference image."),new Rect(0.1,0.2,0.5,0.6));Layout(cropWindow,900,760);
                        if((cropWindow.Crop.TopLeft-new Point(0.1,0.2)).Length>0.01||Math.Abs(cropWindow.Crop.Width-0.5)>0.01)throw new Exception("Crop window lost the crop.");
                        cropWindow.Crop=new Rect(-1,-1,5,5);if(cropWindow.Crop.Left<0||cropWindow.Crop.Right>1.0001)throw new Exception("Crop window did not clamp to the photo.");
                    }
                    var content=(FrameworkElement)workspace.Content;content.Measure(new Size(1250,800));content.Arrange(new Rect(0,0,1250,800));content.UpdateLayout();
                    var screenshot=new RenderTargetBitmap(1250,800,96,96,PixelFormats.Pbgra32);screenshot.Render(content);
                    using(var screenshotFile=File.Create(Path.Combine(output,"editor.png"))){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(screenshot));encoder.Save(screenshotFile);}
                    var geometry=(MeshGeometry3D)custom.Geometry[0].Geometry;
                    var point=geometry.Positions[0];point.Z+=0.01;geometry.Positions[0]=point;
                    await custom.ExportAsync(Path.Combine(output,"edited-face"));
                    Console.WriteLine("PASS custom .big import, byte-identical package export and edited native export: "+output);
                    app.Shutdown(0);return;
                }
                var catalog=JsonSerializer.Deserialize<List<Template>>(File.ReadAllText(Path.Combine(templates,"catalog.json")))!;
                int count=0;StudioModel? firstHead=null,firstHair=null,hairHead=null;
                foreach(var template in catalog)
                {
                    var model=await StudioModel.OpenAsync(ModelDocument.ContainedPath(templates,template.Model),converter);
                    await model.ExportAsync(Path.Combine(output,"template-"+count));
                    if(!File.ReadAllBytes(model.SourcePath).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output,"template-"+count,"model.o"))))throw new Exception("No-edit export is not byte-identical.");
                    SavePreview(model,Path.Combine(templates,"previews",template.Category+"-"+template.Sha256[..12]+".png"));
                    Console.WriteLine($"PASS .o round-trip: {template.Name} ({model.Document.Parts.Sum(p=>p.Positions.Length)} vertices)");
                    if(template.Category=="Head")firstHead??=model;else firstHair??=model;
                    if(template.Model.Contains("/6059/"))hairHead=model;
                    count++;
                }
                if(firstHead is null||firstHair is null)throw new Exception("Missing head or hair templates.");
                var main=new MainWindow();Layout(main,960,650);
                var browser=new TextureWindow();Layout(browser,1220,800);
                var editor=new FaceSculptWindow(firstHead,firstHair);Layout(editor,1280,840);
                {
                    // 3D view: zoom at a cursor point keeps the world point under it; the three-quarter view and Fit frame the head.
                    var nav=BindingFlags.Instance|BindingFlags.NonPublic;
                    typeof(FaceSculptWindow).GetMethod("SetView",nav)!.Invoke(editor,new object[]{"front"});
                    var viewport=(HelixToolkit.Wpf.HelixViewport3D)editor.FindName("Viewport");
                    Point3D Under(Point p){var c=(OrthographicCamera)viewport.Camera;var f=c.LookDirection;f.Normalize();var r=Vector3D.CrossProduct(f,c.UpDirection);r.Normalize();var u=Vector3D.CrossProduct(r,f);double s=c.Width/viewport.ActualWidth;return c.Position+r*(p.X-viewport.ActualWidth/2)*s+u*(viewport.ActualHeight/2-p.Y)*s;}
                    var cursor=new Point(viewport.ActualWidth*0.3,viewport.ActualHeight*0.4);var before=Under(cursor);
                    typeof(FaceSculptWindow).GetMethod("ZoomAt",nav)!.Invoke(editor,new object[]{cursor,3.0});
                    double pixel=((OrthographicCamera)viewport.Camera).Width/viewport.ActualWidth;
                    if((Under(cursor)-before).Length>pixel)throw new Exception("Zoom moved the point under the cursor.");
                    typeof(FaceSculptWindow).GetMethod("SetView",nav)!.Invoke(editor,new object[]{"threequarter"});
                    var look=((OrthographicCamera)viewport.Camera).LookDirection;if(!(look.X<0&&look.Z<0))throw new Exception("Three-quarter view looks the wrong way.");
                    typeof(FaceSculptWindow).GetMethod("SetView",nav)!.Invoke(editor,new object[]{"front"});
                }
                // Likeness: with untouched default markers the photo layout equals the head's, so the fit must not move anything.
                var headPositions=firstHead.Document.Parts.Select(p=>p.Positions.ToArray()).ToArray();
                var likeness=new LikenessWindow(firstHead.Geometry,firstHead.Document.Parts,new[]{0},headPositions,headPositions.SelectMany(p=>p).Min(v=>v.Y)+2);Layout(likeness,1320,900);
                var blank=BitmapSource.Create(600,800,96,96,PixelFormats.Bgra32,null,new byte[600*800*4],600*4);
                likeness.LoadPhoto(false,blank);likeness.LoadPhoto(true,blank);
                if(likeness.Fit().Zip(headPositions).SelectMany(z=>z.First.Zip(z.Second,Vector3.Distance)).Max()>1e-3f)throw new Exception("Likeness fit moved the head with an unchanged layout.");
                // Automatic front markers: no face in a blank photo, a face in a front render of the stock head.
                LikenessWindow.ModelPath=Path.Combine(studio,"vendor","facemesh","face_landmarks.onnx");
                if(likeness.AutoPlaceFront())throw new Exception("Found a face in a blank photo.");
                likeness.LoadPhoto(false,FrontRender(firstHead));
                if(!likeness.AutoPlaceFront())throw new Exception("No face found in a front render of the stock head.");
                // Image panels: zooming 4x keeps markers the same size on screen and does not change the fit.
                Layout(likeness,1320,900);
                var fitBefore=likeness.Fit();
                var photoZoom=(ZoomPanel)likeness.FindName("FrontPhotoZoom");photoZoom.ZoomBy(4);
                if(Math.Abs(photoZoom.Magnification-4)>1e-3)throw new Exception($"Photo panel zoom is {photoZoom.Magnification}, expected 4.");
                var photoMarker=((Canvas)likeness.FindName("FrontPhotoCanvas")).Children.OfType<FrameworkElement>().First();
                if(photoMarker.RenderTransform is not ScaleTransform{ScaleX:var markerScale}||Math.Abs(photoMarker.Width*markerScale*photoZoom.Scale-ZoomPanel.MarkerScreenSize)>1e-3)throw new Exception("Markers do not keep their on-screen size while zoomed.");
                if(likeness.Fit().Zip(fitBefore).SelectMany(z=>z.First.Zip(z.Second,Vector3.Distance)).Max()>1e-5f)throw new Exception("Zooming a panel changed the fit.");
                photoZoom.Fit();if(Math.Abs(photoZoom.Magnification-1)>1e-3)throw new Exception("Fit did not restore the whole photo.");
                {
                    // Likeness accuracy with a known answer: reshape stock head 1188 (wider jaw, longer chin, wider nose, eyes
                    // further apart), render that as the "photo", fit the ORIGINAL head to it and measure how much of the known
                    // change was recovered, straight-on and turned. Numbers are mean landmark error in mesh units.
                    var all=headPositions.SelectMany(p=>p).ToArray();float minY=all.Min(v=>v.Y),maxY=all.Max(v=>v.Y),midX=(all.Min(v=>v.X)+all.Max(v=>v.X))/2,H=maxY-minY,frontZ=all.Max(v=>v.Z),depthZ=frontZ-all.Min(v=>v.Z);
                    float Bump(float t,float centre,float width){float d=(t-centre)/width;return MathF.Exp(-d*d);}
                    Vector3 Reshape(Vector3 v)
                    {
                        float t=(v.Y-minY)/H,face=Math.Clamp((v.Z-(frontZ-depthZ*0.55f))/(depthZ*0.2f),0,1); // front half of the head only
                        float x=midX+(v.X-midX)*(1+face*(0.16f*Bump(t,0.38f,0.10f)+0.10f*Bump(t,0.62f,0.05f)));     // jaw wider, eye line wider
                        float y=v.Y-face*H*0.035f*Bump(t,0.30f,0.09f);                                               // chin longer
                        return new Vector3(x,y,v.Z+face*depthZ*0.05f*Bump(t,0.50f,0.05f)*Bump((v.X-midX)/H,0,0.06f)); // nose out
                    }
                    var truth=headPositions.Select(p=>p.Select(Reshape).ToArray()).ToArray();
                    Vector3[] Marks(Vector3[][] pos)=>FaceFit.Landmarks.Where(l=>l.Front).Select(l=>FaceFit.AnchorAtUv(firstHead.Document.Parts,new[]{0},l.Uv)!.Value.Position(pos)).ToArray();
                    var goal=Marks(truth);
                    double Flat(Vector3[] a){var from=a.Select(v=>new Vector2(v.X,v.Y)).ToArray();var to=goal.Select(v=>new Vector2(v.X,v.Y)).ToArray();var (s,o)=FaceFit.Align(from,to);return from.Zip(to,(p,q)=>Vector2.Distance(p*s+o,q)).Average();}
                    double Solid(Vector3[] a){var t=DenseFit.Similarity(a,goal);return a.Zip(goal,(p,q)=>Vector3.Distance(t(p),q)).Average();}
                    var start=Marks(headPositions);
                    Console.WriteLine($"LIKENESS unfitted: front-view error {Flat(start):F3}, 3D error {Solid(start):F3}");
                    // Render the reshaped head by moving the model's preview meshes, then put them back.
                    var meshBefore=firstHead.Geometry.Select(g=>((MeshGeometry3D)g.Geometry).Positions).ToArray();
                    for(int p=0;p<truth.Length;p++)((MeshGeometry3D)firstHead.Geometry[p].Geometry).Positions=new Point3DCollection(truth[p].Select(v=>new Point3D(v.X,v.Y,v.Z)));
                    var photos=new[]{("straight",0.0,0.0),("turned 12 deg",12.0,0.0),("turned 10 + tilted 8 deg",10.0,8.0)}.Select(c=>(c.Item1,FrontRender(firstHead,c.Item2,c.Item3))).ToArray();
                    for(int p=0;p<truth.Length;p++)((MeshGeometry3D)firstHead.Geometry[p].Geometry).Positions=meshBefore[p];
                    foreach(var (label,photo) in photos)
                    {
                        var window=new LikenessWindow(firstHead.Geometry,firstHead.Document.Parts,new[]{0},headPositions,minY+2);Layout(window,1320,900);
                        window.LoadPhoto(false,photo);
                        if(!window.AutoPlaceFront())throw new Exception($"No face found in the {label} render.");
                        var dense=(CheckBox)window.FindName("Dense");
                        dense.IsChecked=false;var byMarkers=Marks(window.Fit());
                        dense.IsChecked=true;var byDense=Marks(window.Fit());
                        ((Slider)window.FindName("DepthStrength")).Value=0;var flatOnly=Marks(window.Fit());
                        Console.WriteLine($"LIKENESS {label}: markers {Flat(byMarkers):F3} / {Solid(byMarkers):F3}; dense {Flat(byDense):F3} / {Solid(byDense):F3}; dense without depth {Flat(flatOnly):F3} / {Solid(flatOnly):F3}  (front-view / 3D)");
                        if(!(Flat(byDense)<Flat(start)*0.85&&Flat(byDense)<Flat(byMarkers)))throw new Exception($"Automatic likeness fit ({label}) did not recover the known reshaping ({Flat(start):F3} -> {Flat(byDense):F3}).");
                        window.Close();
                    }
                    {
                        // A front marker moved by hand corrects the automatic fit there: left in place it changes nothing; the cheekbone
                        // markers dragged 10 px outward widen the cheekbones by about that much on the head.
                        var t=truth.SelectMany(p=>p).ToArray();float w=(t.Max(v=>v.Y)-t.Min(v=>v.Y))*1.3f;
                        var window=new LikenessWindow(firstHead.Geometry,firstHead.Document.Parts,new[]{0},headPositions,minY+2);Layout(window,1320,900);
                        window.LoadPhoto(false,photos[0].Item2);
                        if(!window.AutoPlaceFront())throw new Exception("No face found in the straight render.");
                        var auto=Marks(window.Fit());
                        var fronts=Enumerable.Range(0,FaceFit.Landmarks.Length).Where(i=>FaceFit.Landmarks[i].Front).ToArray();var canvas=(Canvas)window.FindName("FrontPhotoCanvas");
                        Vector2 At(int i){var m=(FrameworkElement)canvas.Children[Array.IndexOf(fronts,i)];return new Vector2((float)(Canvas.GetLeft(m)+m.Width/2),(float)(Canvas.GetTop(m)+m.Height/2));}
                        window.SetPhotoMarker(false,19,At(19));window.SetPhotoMarker(false,20,At(20));
                        if(Marks(window.Fit()).Zip(auto,Vector3.Distance).Max()>1e-3f)throw new Exception("Front markers confirmed in place changed the automatic fit.");
                        window.SetPhotoMarker(false,19,At(19)+new Vector2(-10,0));window.SetPhotoMarker(false,20,At(20)+new Vector2(10,0));
                        var wider=Marks(window.Fit());window.Close();
                        int left=Array.IndexOf(fronts,19),right=Array.IndexOf(fronts,20);
                        float gained=(wider[right].X-wider[left].X)-(auto[right].X-auto[left].X),expected=20*w/600;
                        Console.WriteLine("LIKENESS front marker correction: cheekbones widened by "+gained.ToString("F3")+" units, expected about "+expected.ToString("F3"));
                        if(!(gained>expected*0.75f&&gained<expected*1.25f))throw new Exception("Dragged front markers did not move the head by the dragged amount.");
                    }
                    {
                        // Profile with a known answer: nose, chin and brow pushed forward as well, seen from the front and from the
                        // side (facing right, so the mirroring is covered too). Side-view error = mean landmark error in depth and height.
                        Vector3 Forward(Vector3 v){var r=Reshape(v);float t=(v.Y-minY)/H,face=Math.Clamp((v.Z-(frontZ-depthZ*0.55f))/(depthZ*0.2f),0,1);return r with{Z=r.Z+face*depthZ*(0.07f*Bump(t,0.30f,0.08f)+0.05f*Bump(t,0.50f,0.05f)+0.04f*Bump(t,0.68f,0.05f))};}
                        var truth2=headPositions.Select(p=>p.Select(Forward).ToArray()).ToArray();var goal2=Marks(truth2);
                        double Side(Vector3[] a){var from=a.Select(v=>new Vector2(v.Z,v.Y)).ToArray();var to=goal2.Select(v=>new Vector2(v.Z,v.Y)).ToArray();var (s,o)=FaceFit.Align(from,to);return from.Zip(to,(p,q)=>Vector2.Distance(p*s+o,q)).Average();}
                        for(int p=0;p<truth2.Length;p++)((MeshGeometry3D)firstHead.Geometry[p].Geometry).Positions=new Point3DCollection(truth2[p].Select(v=>new Point3D(v.X,v.Y,v.Z)));
                        var front2=FrontRender(firstHead);var side2=FrontRender(firstHead,90);
                        for(int p=0;p<truth2.Length;p++)((MeshGeometry3D)firstHead.Geometry[p].Geometry).Positions=meshBefore[p];
                        var window=new LikenessWindow(firstHead.Geometry,firstHead.Document.Parts,new[]{0},headPositions,minY+2);Layout(window,1320,900);
                        window.LoadPhoto(false,front2);
                        if(!window.AutoPlaceFront())throw new Exception("No face found in the straight render.");
                        var frontOnly=Marks(window.Fit());
                        window.LoadPhoto(true,side2);
                        if(!window.AutoPlaceSide())throw new Exception("No face found in the side render.");
                        if(Marks(window.Fit()).Zip(frontOnly,Vector3.Distance).Max()>1e-5f)throw new Exception("Side markers that were only auto-placed changed the fit.");
                        window.Close();
                        // Side markers on the exact spots (a careful user) must recover the profile.
                        var manual=new LikenessWindow(firstHead.Geometry,firstHead.Document.Parts,new[]{0},headPositions,minY+2);Layout(manual,1320,900);
                        manual.LoadPhoto(false,front2);manual.AutoPlaceFront();
                        manual.LoadPhoto(true,BitmapSource.Create(600,600,96,96,PixelFormats.Bgra32,null,new byte[600*600*4],600*4));
                        for(int i=0;i<FaceFit.Landmarks.Length;i++)if(FaceFit.Landmarks[i].Side&&FaceFit.AnchorAtUv(firstHead.Document.Parts,new[]{0},FaceFit.Landmarks[i].Uv) is {} a){var q=a.Position(truth2);manual.SetPhotoMarker(true,i,new Vector2(300-q.Z*20,300-(q.Y-(minY+maxY)/2)*20));}
                        var bySide=Marks(manual.Fit());manual.Close();
                        Console.WriteLine("LIKENESS profile: side-view error unfitted "+Side(start).ToString("F3")+", front photo only "+Side(frontOnly).ToString("F3")+", with side markers "+Side(bySide).ToString("F3"));
                        if(!(Side(bySide)<Side(frontOnly)*0.8))throw new Exception("Side markers did not recover the known profile.");
                    }
                }
                var mesh=(MeshGeometry3D)firstHead.Geometry[0].Geometry;
                int highest=Enumerable.Range(0,mesh.Positions.Count).MaxBy(i=>mesh.Positions[i].Y);
                var original=mesh.Positions[highest];
                var hairBefore=((MeshGeometry3D)firstHair.Geometry[0].Geometry).Positions.ToArray();
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                typeof(FaceSculptWindow).GetField("_center",flags)!.SetValue(editor,new Vector3((float)original.X,(float)original.Y,(float)original.Z));
                Invoke(editor,"OnMove",new Button{Tag="0,0,1"});
                if(mesh.Positions[highest].Z<=original.Z)throw new Exception("Sculpt movement failed.");
                if(((MeshGeometry3D)firstHair.Geometry[0].Geometry).Positions.SequenceEqual(hairBefore))throw new Exception("Hair did not follow a scalp sculpt.");
                Invoke(editor,"OnUndo",editor);if(mesh.Positions[highest]!=original)throw new Exception("Undo failed.");
                Invoke(editor,"OnRedo",editor);if(mesh.Positions[highest].Z<=original.Z)throw new Exception("Redo failed.");
                string project=Path.Combine(output,"test.c07face.json");editor.SaveProject(project);
                var edited=mesh.Positions[highest];Invoke(editor,"OnUndo",editor);editor.LoadProject(project);
                if(mesh.Positions[highest]!=edited)throw new Exception("Project reload did not restore edited vertices.");
                await firstHead.ExportAsync(Path.Combine(output,"sculpted-head"));
                string package=Path.Combine(output,"sculpted-face.big");
                File.WriteAllBytes(package,StudioModel.BuildFacePackage(Path.Combine(output,"sculpted-head")));
                // Full reopen of the saved .big: BIG directory, OTools decode and native vertex-buffer binding.
                var reopenedPackage=await StudioModel.OpenAsync(package,converter);
                var sculpted=firstHead.Document.Parts[0].Positions;var decoded=reopenedPackage.Document.Parts[0].Positions;
                if(decoded.Length!=sculpted.Length||decoded.Zip(sculpted).Any(z=>Vector3.Distance(z.First,z.Second)>0.001f))throw new Exception("Face package does not contain the sculpted head.");
                reopenedPackage.DeleteWorkspace();
                // Larger face texture on the stock head: the export rebuilds textures.fsh at 512 and the package reopens at 512.
                var stockFace=firstHead.CurrentTexture(0);
                var face512=new TransformedBitmap(stockFace,new ScaleTransform(512.0/stockFace.PixelWidth,512.0/stockFace.PixelHeight));
                firstHead.Wrap(0,face512);
                await firstHead.ExportAsync(Path.Combine(output,"sculpted-head-512"));
                string package512=Path.Combine(output,"sculpted-face-512.big");
                File.WriteAllBytes(package512,StudioModel.BuildFacePackage(Path.Combine(output,"sculpted-head-512")));
                var reopened512=await StudioModel.OpenAsync(package512,converter);
                if(reopened512.CurrentTexture(0).PixelWidth!=512)throw new Exception("512 face texture did not survive export.");
                reopened512.DeleteWorkspace();
                {
                    // Hair on a stock head that has a hair mesh (6059, long hair): Volume + makes the hair fuller and leaves the face,
                    // a blonde recolour and the new shape survive Save as .big and reopening.
                    if(hairHead is null)throw new Exception("Stock head 6059 (with hair) is missing from the templates.");
                    var hairEditor=new FaceSculptWindow(hairHead);Layout(hairEditor,1280,840);
                    var nav=BindingFlags.Instance|BindingFlags.NonPublic;
                    int hairPart=(int)typeof(FaceSculptWindow).GetProperty("HairPart",nav)!.GetValue(hairEditor)!;
                    if(hairPart<0)throw new Exception("Hair mesh not found on stock head 6059.");
                    int facePart=hairHead.Document.Parts.FindIndex(p=>Path.GetFileNameWithoutExtension(p.ImageUri)=="face");
                    Point3D[] Mesh(int part)=>((MeshGeometry3D)hairHead.Geometry[part].Geometry).Positions.ToArray();
                    var hairStart=Mesh(hairPart);var faceBefore=Mesh(facePart);
                    var c=new Point3D(hairStart.Average(p=>p.X),hairStart.Average(p=>p.Y),hairStart.Average(p=>p.Z));
                    double Spread(Point3D[] pts)=>pts.Average(p=>(p-c).Length);
                    ((CheckBox)hairEditor.FindName("WholeHair")).IsChecked=true;
                    typeof(FaceSculptWindow).GetMethod("OnHairVolume",nav)!.Invoke(hairEditor,new object[]{new Button{Tag="1"},new RoutedEventArgs()});
                    var hairEnd=Mesh(hairPart);
                    if(!(Spread(hairEnd)>Spread(hairStart)*1.005))throw new Exception("Volume + did not make the hair fuller.");
                    if(!faceBefore.SequenceEqual(Mesh(facePart)))throw new Exception("Hair volume changed the face.");
                    var hairTexture=hairHead.CurrentTexture(hairPart);var (px,w,h)=PixelsOf(hairTexture);
                    var blonde=(byte[])px.Clone();for(int i=0;i<blonde.Length;i+=4)if(blonde[i+3]>0){blonde[i+2]=(byte)Math.Min(255,blonde[i+2]+140);blonde[i+1]=(byte)Math.Min(255,blonde[i+1]+100);blonde[i]=(byte)Math.Min(255,blonde[i]+40);} // a plain blonde tint: the change must survive Save as .big
                    hairHead.Wrap(hairPart,BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,blonde,w*4));
                    await hairHead.ExportAsync(Path.Combine(output,"hair-head"));
                    string hairPackage=Path.Combine(output,"hair-head.big");File.WriteAllBytes(hairPackage,StudioModel.BuildFacePackage(Path.Combine(output,"hair-head")));
                    var reopenedHair=await StudioModel.OpenAsync(hairPackage,converter);
                    var savedHair=reopenedHair.Document.Parts[hairPart].Positions;var editedHair=hairHead.Document.Parts[hairPart].Positions;
                    if(savedHair.Zip(editedHair).Any(z=>Vector3.Distance(z.First,z.Second)>0.001f))throw new Exception("Saved hair shape differs from the edit.");
                    var (rpx,_,_)=PixelsOf(reopenedHair.CurrentTexture(hairPart));
                    double R=0,B=0,n=0;for(int i=0;i<rpx.Length;i+=4)if(rpx[i+3]>128){R+=rpx[i+2];B+=rpx[i];n++;}
                    if(!(n>0&&R/n>120&&R/n>B/n+30))throw new Exception($"Saved hair is not blonde (mean R {R/Math.Max(1,n):F0}, B {B/Math.Max(1,n):F0}).");
                    reopenedHair.DeleteWorkspace();
                    // Paint on the hair mesh in the 3D view: the hair texture changes there, Undo restores it.
                    var hairViewport=(HelixToolkit.Wpf.HelixViewport3D)hairEditor.FindName("Viewport");
                    LayoutContent(hairEditor,1250,800);
                    typeof(FaceSculptWindow).GetMethod("SetView",nav)!.Invoke(hairEditor,new object[]{"front"});
                    var onHair=Enumerable.Range(1,60).Select(i=>new Point(hairViewport.Viewport.ActualWidth/2,hairViewport.Viewport.ActualHeight*i/100.0))
                        .Select(p=>(Point:p,Hit:HitUv(hairEditor,p))).FirstOrDefault(h=>h.Hit?.Part==hairPart);
                    if(onHair.Hit is not { } hairHit)throw new Exception("No hair under the top of the front view. Size "+hairViewport.Viewport.ActualWidth+"x"+hairViewport.Viewport.ActualHeight+" hits: "+string.Join(",",Enumerable.Range(1,60).Select(i=>HitUv(hairEditor,new Point(hairViewport.Viewport.ActualWidth/2,hairViewport.Viewport.ActualHeight*i/100.0))?.Part.ToString()??"-"))+" hair="+hairPart);
                    ((ComboBox)hairEditor.FindName("HeadTool")).SelectedIndex=1;((Slider)hairEditor.FindName("HeadBrushStrength")).Value=1;
                    typeof(FaceSculptWindow).GetMethod("SetHeadColour",nav)!.Invoke(hairEditor,new object[]{new Vector3(1,0,1)});
                    var hairBeforePaint=PixelsOf(hairHead.CurrentTexture(hairPart)).Pixels;
                    if(!(bool)typeof(FaceSculptWindow).GetMethod("BeginPaint",nav)!.Invoke(hairEditor,new object[]{onHair.Point,false})!)throw new Exception("Painting on the hair did not start.");
                    typeof(FaceSculptWindow).GetMethod("FinishPaint",nav)!.Invoke(hairEditor,null);
                    var (hairPainted,hw,hh)=PixelsOf(hairHead.CurrentTexture(hairPart));int hi=((int)(hairHit.Uv.Y*hh)*hw+(int)(hairHit.Uv.X*hw))*4;
                    if(!(hairPainted[hi+2]>hairPainted[hi+1]+40&&hairPainted[hi]>hairPainted[hi+1]+40))throw new Exception("Painting on the hair mesh did not paint the hair texture.");
                    Invoke(hairEditor,"OnUndo",hairEditor);
                    if(!PixelsOf(hairHead.CurrentTexture(hairPart)).Pixels.SequenceEqual(hairBeforePaint))throw new Exception("Undo did not restore the hair texture.");
                    typeof(FaceSculptWindow).GetField("_dirty",nav)!.SetValue(hairEditor,false); // no "unsaved edits" prompt at shutdown
                }
                {
                    // Add hair from a template to a head without a hair mesh (1188, hair painted on): the template takes the
                    // head's shape and face texture, keeps its hair, and the result saves and reopens as a face .big.
                    var nav=BindingFlags.Instance|BindingFlags.NonPublic;
                    var plainEditor=new FaceSculptWindow(firstHead);Layout(plainEditor,1280,840);
                    int[] FaceParts(ModelDocument d)=>d.Parts.Select((p,i)=>(p,i)).Where(x=>Path.GetFileNameWithoutExtension(x.p.ImageUri)=="face").Select(x=>x.i).ToArray();
                    Vector3[][] MeshPositions(StudioModel m)=>m.Geometry.Select(g=>((MeshGeometry3D)g.Geometry).Positions.Select(v=>new Vector3((float)v.X,(float)v.Y,(float)v.Z)).ToArray()).ToArray();
                    var task=(Task<StudioModel>)typeof(FaceSculptWindow).GetMethod("AddHairAsync",nav)!.Invoke(plainEditor,new object[]{ModelDocument.ContainedPath(templates,"heads/23525/model.o")})!;
                    var withHair=await task;
                    if(withHair.Document.Parts.All(p=>Path.GetFileNameWithoutExtension(p.ImageUri)!="hair"))throw new Exception("Add hair: the result has no hair mesh.");
                    if(withHair.SaveName!="1188")throw new Exception($"Add hair: suggested save name is {withHair.SaveName}, expected 1188.");
                    var sourceMesh=MeshPositions(firstHead);var resultMesh=MeshPositions(withHair);
                    double landmarkGap=FaceFit.Landmarks.Select(l=>Vector3.Distance(FaceFit.AnchorAtUv(firstHead.Document.Parts,FaceParts(firstHead.Document),l.Uv)!.Value.Position(sourceMesh),FaceFit.AnchorAtUv(withHair.Document.Parts,FaceParts(withHair.Document),l.Uv)!.Value.Position(resultMesh))).Max();
                    if(landmarkGap>0.05)throw new Exception($"Add hair: landmarks are {landmarkGap:F3} units from the source head.");
                    var sourceTexture=firstHead.CurrentTexture(FaceParts(firstHead.Document)[0]);var resultTexture=withHair.CurrentTexture(FaceParts(withHair.Document)[0]);
                    if(resultTexture.PixelWidth!=sourceTexture.PixelWidth)throw new Exception("Add hair: the face texture was not carried over.");
                    await withHair.ExportAsync(Path.Combine(output,"added-hair"));
                    string addedPackage=Path.Combine(output,"added-hair.big");File.WriteAllBytes(addedPackage,StudioModel.BuildFacePackage(Path.Combine(output,"added-hair")));
                    var reopenedAdded=await StudioModel.OpenAsync(addedPackage,converter);
                    if(reopenedAdded.Document.Parts.All(p=>Path.GetFileNameWithoutExtension(p.ImageUri)!="hair"))throw new Exception("Add hair: the saved .big lost the hair mesh.");
                    var savedPositions=reopenedAdded.Document.Parts.SelectMany(p=>p.Positions).ToArray();var expected=resultMesh.SelectMany(p=>p).ToArray();
                    if(savedPositions.Zip(expected).Any(z=>Vector3.Distance(z.First,z.Second)>0.001f))throw new Exception("Add hair: the saved head differs from the result.");
                    reopenedAdded.DeleteWorkspace();
                    {
                        // Paint on the head in the 3D view: close the gap between the template's hair mesh and the forehead by
                        // painting dark hair just below the hair edge. Live preview, texture change, unified undo/redo with
                        // sculpting, and the paint is inside the saved .big.
                        var painter=new FaceSculptWindow(withHair);Layout(painter,1280,840);
                        typeof(FaceSculptWindow).GetMethod("SetView",nav)!.Invoke(painter,new object[]{"front"});
                        LayoutContent(painter,1250,800);
                        var view=((HelixToolkit.Wpf.HelixViewport3D)painter.FindName("Viewport")).Viewport;
                        int face=FaceParts(withHair.Document)[0];
                        double cx=view.ActualWidth/2;
                        // First face pixel below the hair, going down the middle of the front view.
                        double edge=Enumerable.Range(1,70).Select(i=>view.ActualHeight*i/100.0).First(y=>HitUv(painter,new Point(cx,y)) is {Part:var p}&&Path.GetFileNameWithoutExtension(withHair.Document.Parts[p].ImageUri)=="face");
                        var beforeRender=FrontRender(withHair);
                        ((ComboBox)painter.FindName("HeadTool")).SelectedIndex=1;((Slider)painter.FindName("HeadBrushStrength")).Value=0.9;
                        typeof(FaceSculptWindow).GetMethod("SetHeadColour",nav)!.Invoke(painter,new object[]{new Vector3(0.07f,0.055f,0.05f)});
                        var (faceBefore,fw,fh)=PixelsOf(withHair.CurrentTexture(face));
                        var begin=typeof(FaceSculptWindow).GetMethod("BeginPaint",nav)!;var cont=typeof(FaceSculptWindow).GetMethod("ContinuePaint",nav)!;
                        var start=new Point(cx-view.ActualWidth*0.07,edge+4);
                        if(!(bool)begin.Invoke(painter,new object[]{start,false})!)throw new Exception("Painting on the head did not start.");
                        var faceMaterial=(DiffuseMaterial)withHair.Geometry[face].Material;
                        if(faceMaterial.Brush is not ImageBrush{ImageSource:WriteableBitmap})throw new Exception("The head does not show the stroke live while painting.");
                        for(int i=1;i<=20;i++)cont.Invoke(painter,new object[]{new Point(start.X+view.ActualWidth*0.14*i/20,edge+4)});
                        typeof(FaceSculptWindow).GetMethod("FinishPaint",nav)!.Invoke(painter,null);
                        var centreHit=HitUv(painter,new Point(cx,edge+4))!.Value;int ci=((int)(centreHit.Uv.Y*fh)*fw+(int)(centreHit.Uv.X*fw))*4;
                        var (facePainted,_,_)=PixelsOf(withHair.CurrentTexture(face));
                        if(!(facePainted[ci+2]<60&&faceBefore[ci+2]>facePainted[ci+2]+20))throw new Exception($"Forehead below the hair was not painted dark (R {faceBefore[ci+2]} -> {facePainted[ci+2]}).");
                        SaveSideBySide(Path.Combine(output,"hairline-paint-before-after.png"),beforeRender.PixelWidth,beforeRender.PixelHeight,PixelsOf(beforeRender).Pixels,PixelsOf(FrontRender(withHair)).Pixels);
                        // Unified undo: sculpt after painting, then undo twice (sculpt, then paint) and redo the paint.
                        var meshBefore=((MeshGeometry3D)withHair.Geometry[face].Geometry).Positions.ToArray();
                        var top=withHair.Document.Parts[face].Positions.MaxBy(v=>v.Y);
                        typeof(FaceSculptWindow).GetField("_center",nav)!.SetValue(painter,top);Invoke(painter,"OnMove",new Button{Tag="0,0,1"});
                        Invoke(painter,"OnUndo",painter);
                        if(!meshBefore.SequenceEqual(((MeshGeometry3D)withHair.Geometry[face].Geometry).Positions))throw new Exception("Undo did not remove the sculpt.");
                        if(!PixelsOf(withHair.CurrentTexture(face)).Pixels.SequenceEqual(facePainted))throw new Exception("Undoing the sculpt changed the paint.");
                        Invoke(painter,"OnUndo",painter);
                        if(!PixelsOf(withHair.CurrentTexture(face)).Pixels.SequenceEqual(faceBefore))throw new Exception("Undo did not remove the paint stroke.");
                        Invoke(painter,"OnRedo",painter);
                        if(!PixelsOf(withHair.CurrentTexture(face)).Pixels.SequenceEqual(facePainted))throw new Exception("Redo did not bring the paint stroke back.");
                        {
                            // Paint keeps the texture's detail; I picks the colour under the cursor; the outline matches the brush.
                            var tools=(ComboBox)painter.FindName("HeadTool");
                            if(tools.Items.Cast<ComboBoxItem>().Any(i=>(string?)i.Tag is "Darken" or "Lighten" or "Push"||((string)i.Content).StartsWith("Pick")))throw new Exception("Removed tools are still listed.");
                            tools.SelectedIndex=tools.Items.Cast<ComboBoxItem>().ToList().FindIndex(i=>(string?)i.Tag=="Pins"); // I must switch to Paint from any tool
                            var cheek=new Point(cx+view.ActualWidth*0.1,view.ActualHeight*0.45);
                            if(!(bool)typeof(FaceSculptWindow).GetMethod("PickUnderCursor",nav)!.Invoke(painter,new object[]{cheek})!)throw new Exception("I over the head did not pick a colour.");
                            var picked=(Vector3)typeof(FaceSculptWindow).GetField("_headColour",nav)!.GetValue(painter)!;
                            var cheekUv=HitUv(painter,cheek)!.Value.Uv;int ci2=((int)(cheekUv.Y*fh)*fw+(int)(cheekUv.X*fw))*4;
                            if(Math.Abs(picked.X*255-facePainted[ci2+2])>1.5||Math.Abs(picked.Z*255-facePainted[ci2])>1.5)throw new Exception("I picked a different colour than the texture under the cursor.");
                            if((string?)((ComboBoxItem)tools.SelectedItem).Tag!="Paint")throw new Exception("I did not switch to Paint.");
                            Console.WriteLine($"PASS I eyedropper: picked #{(int)(picked.X*255):X2}{(int)(picked.Y*255):X2}{(int)(picked.Z*255):X2} under the cursor and switched to Paint");
                            {
                                // Pick feedback: a ring at the cursor, new colour on top, previous colour below; frozen at full size for a screenshot.
                                var cursorLayer=(Canvas)painter.FindName("CursorLayer");
                                var fb=cursorLayer.Children.OfType<Grid>().LastOrDefault(g=>(string?)g.Tag=="pick-feedback")??throw new Exception("No colour ring appeared when picking.");
                                var topColour=((SolidColorBrush)((System.Windows.Shapes.Ellipse)fb.Children[1]).Stroke).Color;
                                if(Math.Abs(topColour.R-picked.X*255)>1||Math.Abs(topColour.B-picked.Z*255)>1)throw new Exception("The colour ring does not show the picked colour.");
                                double ringX=Canvas.GetLeft(fb)+fb.Width/2,ringY=Canvas.GetTop(fb)+fb.Height/2;
                                if(Math.Abs(ringX-cheek.X)>2||Math.Abs(ringY-cheek.Y)>2)throw new Exception($"The colour ring is not at the cursor ({ringX:F0},{ringY:F0} vs {cheek.X:F0},{cheek.Y:F0}).");
                                var st=(ScaleTransform)fb.RenderTransform;st.BeginAnimation(ScaleTransform.ScaleXProperty,null);st.BeginAnimation(ScaleTransform.ScaleYProperty,null);st.ScaleX=st.ScaleY=1;
                                fb.BeginAnimation(UIElement.OpacityProperty,null);fb.Opacity=1;
                                SaveWindow(painter,Path.Combine(output,"pick-ring.png"),1250,800);
                                cursorLayer.Children.Remove(fb);
                                Console.WriteLine("PASS pick ring: shown at the cursor with the picked colour on top");
                            }
                            // Paint a dark beard colour over the stubble around the mouth: colour changes, fine detail stays.
                            ((Slider)painter.FindName("HeadBrushStrength")).Value=1;((Slider)painter.FindName("HeadBrushSize")).Value=40;
                            typeof(FaceSculptWindow).GetMethod("SetHeadColour",nav)!.Invoke(painter,new object[]{new Vector3(0.12f,0.08f,0.06f)});
                            var mouth=new Point(cx,view.ActualHeight*0.5);
                            begin.Invoke(painter,new object[]{mouth,false});for(int i=1;i<=10;i++)cont.Invoke(painter,new object[]{new Point(mouth.X+i*3,mouth.Y)});
                            typeof(FaceSculptWindow).GetMethod("FinishPaint",nav)!.Invoke(painter,null);
                            var painted2=PixelsOf(withHair.CurrentTexture(face)).Pixels;
                            var region=Enumerable.Range(0,painted2.Length/4).Where(p=>Math.Abs(painted2[p*4+2]-facePainted[p*4+2])>40&&p%fw<fw-1&&p/fw<fh-1).ToArray();
                            // High-pass detail (pixel against its 4 neighbours) over the painted core only, so the brush edge and smooth shading do not count.
                            var inRegion=new HashSet<int>(region);var core=region.Where(p=>inRegion.Contains(p-1)&&inRegion.Contains(p+1)&&inRegion.Contains(p-fw)&&inRegion.Contains(p+fw)).ToArray();
                            double Detail(byte[] img)=>core.Average(p=>Math.Abs(4*img[p*4+2]-img[(p-1)*4+2]-img[(p+1)*4+2]-img[(p-fw)*4+2]-img[(p+fw)*4+2]));
                            double darkness=region.Average(p=>painted2[p*4+2]),detail=Detail(painted2)/Detail(facePainted);
                            Console.WriteLine($"PASS paint keeps detail: {region.Length} pixels painted to mean R {darkness:F0}, {detail:P0} of the fine detail kept (high-pass {Detail(facePainted):F1} -> {Detail(painted2):F1})");
                            {var at=HitUv(painter,new Point(mouth.X+15,mouth.Y))!.Value.Uv*new Vector2(fw,fh);int cs=64,z=6,ox=Math.Clamp((int)at.X-cs/2,0,fw-cs),oy=Math.Clamp((int)at.Y-cs/2,0,fh-cs);
                             byte[] Crop(byte[] src){var o=new byte[cs*z*cs*z*4];for(int y=0;y<cs*z;y++)for(int x=0;x<cs*z;x++)Buffer.BlockCopy(src,((oy+y/z)*fw+ox+x/z)*4,o,(y*cs*z+x)*4,4);return o;}
                             SaveSideBySide(Path.Combine(output,"paint-zoom.png"),cs*z,cs*z,Crop(facePainted),Crop(painted2));}
                            if(region.Length<100||darkness>70||detail<0.5)throw new Exception($"Paint: expected dark paint with the texture detail kept ({region.Length} px, R {darkness:F0}, detail {detail:P0}).");
                            Invoke(painter,"OnUndo",painter);
                            if(!PixelsOf(withHair.CurrentTexture(face)).Pixels.SequenceEqual(facePainted))throw new Exception("Undo did not remove the paint stroke.");
                            // Brush cursor: a circle exactly the slider size (screen px) that follows the cursor; the painted footprint matches it.
                            var ringUpdate=typeof(FaceSculptWindow).GetMethod("UpdateBrushRing",nav)!;var ring=(System.Windows.Shapes.Path)painter.FindName("BrushRing");double RingWidth()=>ring.Data?.Bounds.Width??0;
                            var size=(Slider)painter.FindName("HeadBrushSize");
                            foreach(var px in new[]{20.0,40.0,120.0}){size.Value=px;ringUpdate.Invoke(painter,new object[]{mouth});if(ring.Visibility!=Visibility.Visible||Math.Abs(RingWidth()-px)>1)throw new Exception($"Brush circle is {RingWidth():F1} px for a {px} px brush.");}
                            var widths=Enumerable.Range(0,40).Select(i=>{ringUpdate.Invoke(painter,new object[]{new Point(mouth.X-60+i*3,mouth.Y-20+i)});return RingWidth();}).ToArray();
                            if(widths.Max()-widths.Min()>1e-6)throw new Exception("Brush circle changes size while moving (flicker).");
                            size.Value=40;var texelRadius=typeof(FaceSculptWindow).GetMethod("TexelRadius",nav)!;
                            foreach(var spot in new[]{mouth,new Point(cx,edge+view.ActualHeight*0.15),new Point(cx-view.ActualWidth*0.08,mouth.Y-view.ActualHeight*0.05)})
                            {
                                var c0=HitUv(painter,spot);if(c0 is null)continue;
                                var texels=(float?)texelRadius.Invoke(painter,new object[]{spot,c0.Value.Uv,withHair,withHair.Document.Parts[face].ImageUri!,fw,fh});if(texels is null)continue;
                                var reach=new List<double>();
                                foreach(var dir in new[]{new System.Windows.Vector(1,0),new System.Windows.Vector(-1,0),new System.Windows.Vector(0,1),new System.Windows.Vector(0,-1)})
                                    for(double s=1;s<300;s+=0.5){var h=HitUv(painter,spot+dir*s);if(h is null||h.Value.Part!=c0.Value.Part)break;if(Vector2.Distance(h.Value.Uv,c0.Value.Uv)*fw>=texels){reach.Add(s);break;}}
                                if(reach.Count>0&&(reach.Average()<20*0.75||reach.Average()>20*1.33))throw new Exception($"Painted footprint radius {reach.Average():F1} px does not match the 20 px brush circle radius.");
                                Console.WriteLine($"PASS brush footprint at {spot.X:F0},{spot.Y:F0}: {texels:F1} texels = {string.Join("/",reach.Select(r=>r.ToString("F0")))} px on screen for a 40 px circle");
                            }
                        }
                        {
                            // Pin warp: drag the painted lip line down onto a lower spot, hold an eye, re-grab the pin, undo/redo the
                            // pin layout, and check the moved detail stays sharp (sampled once from the unwarped texture).
                            var tools=(ComboBox)painter.FindName("HeadTool");tools.SelectedIndex=tools.Items.Cast<ComboBoxItem>().ToList().FindIndex(i=>(string?)i.Tag=="Pins");
                            var t=typeof(FaceSculptWindow);bool Begin(Point p)=>(bool)t.GetMethod("BeginPin",nav)!.Invoke(painter,new object[]{p})!;
                            void Drag(Point p)=>t.GetMethod("DragPin",nav)!.Invoke(painter,new object[]{p});void End()=>t.GetMethod("EndPin",nav)!.Invoke(painter,null);
                            int PinCount(){var s=t.GetField("_pins",nav)!.GetValue(painter);return s is null?0:((Array)s.GetType().GetProperty("Pins")!.GetValue(s)!).Length;}
                            var pinStart=PixelsOf(withHair.CurrentTexture(face)).Pixels;var meshStart=((MeshGeometry3D)withHair.Geometry[face].Geometry).Positions.ToArray();
                            var pinBefore=FrontRender(withHair);
                            var lip=new Point(cx,view.ActualHeight*0.5);var eye=new Point(cx-view.ActualWidth*0.06,view.ActualHeight*0.36);
                            if(!Begin(eye))throw new Exception("Pin warp: pressing on the head did not place a pin.");End(); // hold pin
                            if(!PixelsOf(withHair.CurrentTexture(face)).Pixels.SequenceEqual(pinStart))throw new Exception("Pin warp: a hold pin changed the texture.");
                            Begin(lip);for(int i=1;i<=8;i++)Drag(new Point(lip.X,lip.Y+i*2));End();
                            var warped=PixelsOf(withHair.CurrentTexture(face)).Pixels;
                            int moved=Enumerable.Range(0,warped.Length/4).Count(p=>warped[p*4+2]!=pinStart[p*4+2]);
                            if(PinCount()!=2||moved<30)throw new Exception($"Pin warp: expected 2 pins and a moved texture ({PinCount()} pins, {moved} pixels).");
                            if(!meshStart.SequenceEqual(((MeshGeometry3D)withHair.Geometry[face].Geometry).Positions))throw new Exception("Pin warp changed the mesh.");
                            // The held eye did not move.
                            var eyeUv=HitUv(painter,eye)!.Value.Uv;int ei=((int)(eyeUv.Y*fh)*fw+(int)(eyeUv.X*fw))*4;
                            if(warped[ei+2]!=pinStart[ei+2]||warped[ei+1]!=pinStart[ei+1])throw new Exception("Pin warp: the held spot moved.");
                            // Grabbing the pin again (at its new place) moves the same pin, not a new one.
                            Begin(new Point(lip.X,lip.Y+16));Drag(new Point(lip.X,lip.Y+20));End();
                            if(PinCount()!=2)throw new Exception($"Pin warp: re-grabbing a pin made a new one ({PinCount()} pins).");
                            SaveSideBySide(Path.Combine(output,"pin-warp-before-after.png"),pinBefore.PixelWidth,pinBefore.PixelHeight,PixelsOf(pinBefore).Pixels,PixelsOf(FrontRender(withHair)).Pixels);
                            // Detail kept: edge energy of the moved area against the original.
                            var final=PixelsOf(withHair.CurrentTexture(face)).Pixels;
                            var area=Enumerable.Range(0,final.Length/4).Where(p=>final[p*4+2]!=pinStart[p*4+2]&&p%fw<fw-1&&p/fw<fh-1).ToArray();
                            double Edge(byte[] img)=>area.Average(p=>Math.Abs(img[p*4+2]-img[p*4+6])+Math.Abs(img[p*4+2]-img[(p+fw)*4+2]));
                            double kept=Edge(final)/Edge(pinStart);
                            Console.WriteLine($"PASS pin warp: {PinCount()} pins, {area.Length} pixels moved, {kept:P0} of the detail kept, held spot unchanged, mesh unchanged");
                            if(kept<0.8)throw new Exception($"Pin warp softened the texture ({kept:P0} detail left).");
                            // Undo: back to the first drag's result with 2 pins; again: 1 pin, original texture; redo restores 2 pins.
                            Invoke(painter,"OnUndo",painter);
                            if(!PixelsOf(withHair.CurrentTexture(face)).Pixels.SequenceEqual(warped)||PinCount()!=2)throw new Exception("Pin warp undo did not restore the previous pin layout.");
                            Invoke(painter,"OnUndo",painter);Invoke(painter,"OnUndo",painter);
                            if(!PixelsOf(withHair.CurrentTexture(face)).Pixels.SequenceEqual(pinStart)||PinCount()!=0)throw new Exception($"Pin warp undo did not get back to no pins ({PinCount()} pins).");
                            Invoke(painter,"OnRedo",painter);Invoke(painter,"OnRedo",painter);
                            if(!PixelsOf(withHair.CurrentTexture(face)).Pixels.SequenceEqual(warped)||PinCount()!=2)throw new Exception("Pin warp redo did not restore the pins.");
                            t.GetField("_selectedPin",nav)!.SetValue(painter,1);t.GetMethod("RedrawPins",nav)!.Invoke(painter,null);var pinLayer=(Canvas)painter.FindName("PinLayer");
                            typeof(FaceSculptWindow).GetMethod("UpdateBrushRing",nav)!.Invoke(painter,new object[]{new Point(cx+view.ActualWidth*0.12,view.ActualHeight*0.47)});
                            if(pinLayer.Children.OfType<System.Windows.Shapes.Ellipse>().Count(e=>e.Width==12)!=2)throw new Exception($"Pin warp: {pinLayer.Children.Count} handle shapes drawn, expected 2 pins.");
                            SaveWindow(painter,Path.Combine(output,"pin-warp-editor.png"),1250,800);
                            Invoke(painter,"OnUndo",painter);Invoke(painter,"OnUndo",painter);
                            {
                                // Mirror pins: a drag on one cheek also moves the texture at the mirrored spot on the other cheek.
                                var startMirror=PixelsOf(withHair.CurrentTexture(face)).Pixels;
                                var cheek=new Point(cx-view.ActualWidth*0.1,view.ActualHeight*0.45);var cheekUv=HitUv(painter,cheek)!.Value.Uv;
                                Begin(cheek);for(int i=1;i<=6;i++)Drag(new Point(cheek.X,cheek.Y+i*2));End();
                                var afterMirror=PixelsOf(withHair.CurrentTexture(face)).Pixels;
                                int MovedNear(Vector2 uv)=>Enumerable.Range(0,afterMirror.Length/4).Count(p=>afterMirror[p*4+2]!=startMirror[p*4+2]&&Vector2.Distance(new Vector2((p%fw+0.5f)/fw,(p/fw+0.5f)/fh),uv)<0.04f);
                                int here=MovedNear(cheekUv),there=MovedNear(new Vector2(1-cheekUv.X,cheekUv.Y));
                                Console.WriteLine($"PASS mirror pins: {here} pixels moved at the pin, {there} at its mirror");
                                if(here<10||there<10)throw new Exception($"Mirror pins: expected the texture to move on both cheeks ({here} / {there}).");
                                Invoke(painter,"OnUndo",painter);
                                if(!PixelsOf(withHair.CurrentTexture(face)).Pixels.SequenceEqual(startMirror))throw new Exception("Undo did not remove the mirrored pin.");
                            }
                            tools.SelectedIndex=0;
                        }
                        {
                            // Wireframe and single-vertex grab.
                            var t=typeof(FaceSculptWindow);
                            var wireBox=(CheckBox)painter.FindName("ShowWireframe");wireBox.IsChecked=true;t.GetMethod("OnWireframe",nav)!.Invoke(painter,new object[]{wireBox,new RoutedEventArgs()});
                            var wire=(HelixToolkit.Wpf.LinesVisual3D)t.GetField("_wire",nav)!.GetValue(painter)!;
                            int edges=0;foreach(var g in withHair.Geometry){var tri=((MeshGeometry3D)g.Geometry).TriangleIndices;var set=new HashSet<(int,int)>();for(int k=0;k+2<tri.Count;k+=3)for(int q=0;q<3;q++){int a=tri[k+q],b=tri[k+(q+1)%3];set.Add(a<b?(a,b):(b,a));}edges+=set.Count;}
                            if(wire.Points.Count!=edges*2)throw new Exception($"Wireframe has {wire.Points.Count/2} edges, the mesh {edges}.");
                            var vertexBox=(CheckBox)painter.FindName("VertexMode");vertexBox.IsChecked=true;t.GetMethod("OnVertexMode",nav)!.Invoke(painter,new object[]{vertexBox,new RoutedEventArgs()});
                            var lipCorner=new Point(cx+view.ActualWidth*0.03,view.ActualHeight*0.53);
                            var picked=(System.Collections.IList)t.GetMethod("PickVertices",nav)!.Invoke(painter,new object[]{lipCorner})!;
                            var list=picked.Cast<object>().Select(o=>((int Part,int Index,bool Mirror))o).ToList();
                            if(list.Count==0||!list.Any(v=>v.Mirror))throw new Exception($"Single vertex: expected a vertex and its mirror partner ({list.Count} picked).");
                            Point3D[][] Positions()=>withHair.Geometry.Select(g=>((MeshGeometry3D)g.Geometry).Positions.ToArray()).ToArray();
                            var vStart=Positions();
                            t.GetField("_strokeSource",nav)!.SetValue(painter,vStart.Select(p=>p.Select(v=>new Vector3((float)v.X,(float)v.Y,(float)v.Z)).ToArray()).ToArray());
                            float step=(float)(withHair.Geometry.Select(g=>g.Bounds.SizeY).Max()*0.02);
                            var shift=new Vector3(step,step*0.5f,0);
                            var listType=picked.GetType();
                            t.GetMethod("MoveVertices",nav)!.Invoke(painter,new object[]{picked,shift});
                            var after=Positions();int changed=0;
                            for(int p=0;p<after.Length;p++)for(int i=0;i<after[p].Length;i++)
                            {
                                var d=after[p][i]-vStart[p][i];if(d.Length<1e-9)continue;changed++;
                                var v=list.FirstOrDefault(x=>x.Part==p&&x.Index==i);if(!list.Any(x=>x.Part==p&&x.Index==i))throw new Exception("Single vertex moved a vertex that was not picked.");
                                double ex=v.Mirror?-shift.X:shift.X;if(Math.Abs(d.Y-shift.Y)>1e-5||(Math.Abs(d.X-ex)>1e-5&&Math.Abs(d.X)>1e-5))throw new Exception($"Single vertex moved a point by {d}, expected ({ex},{shift.Y},0).");
                            }
                            if(changed!=list.Count)throw new Exception($"Single vertex: {changed} points moved, {list.Count} picked.");
                            t.GetMethod("UpdateWireframe",nav)!.Invoke(painter,null);
                            var moved=list.First(v=>!v.Mirror);if(!wire.Points.Contains(after[moved.Part][moved.Index]))throw new Exception("The wireframe did not follow the moved vertex.");
                            Console.WriteLine($"PASS single vertex: {list.Count} points moved together (seam copies and mirror partner), nothing else; wireframe {edges} edges follows");
                            t.GetMethod("ShowVertexMarkers",nav)!.Invoke(painter,new object[]{picked});
                            // Helix updates screen-space lines on each rendered frame; a hidden test window renders none, so run that update here.
                            foreach(var name in new[]{"UpdateTransforms","UpdateGeometry"})typeof(HelixToolkit.Wpf.ScreenSpaceVisual3D).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)?.Invoke(wire,null);
                            SaveWindow(painter,Path.Combine(output,"wireframe-vertex.png"),1250,800);
                            t.GetMethod("Restore",nav)!.Invoke(painter,new object[]{vStart});
                            wireBox.IsChecked=false;t.GetMethod("OnWireframe",nav)!.Invoke(painter,new object[]{wireBox,new RoutedEventArgs()});vertexBox.IsChecked=false;
                        }
                        await withHair.ExportAsync(Path.Combine(output,"painted-head"));
                        string paintedPackage=Path.Combine(output,"painted-head.big");File.WriteAllBytes(paintedPackage,StudioModel.BuildFacePackage(Path.Combine(output,"painted-head")));
                        var reopenedPainted=await StudioModel.OpenAsync(paintedPackage,converter);
                        var (savedFace,sfw,_)=PixelsOf(reopenedPainted.CurrentTexture(face));
                        if(sfw!=fw||!(savedFace[ci+2]<75))throw new Exception($"The saved .big does not contain the painted hairline (R {savedFace[ci+2]}).");
                        reopenedPainted.DeleteWorkspace();
                        typeof(FaceSculptWindow).GetField("_dirty",nav)!.SetValue(painter,false);
                    }
                    withHair.DeleteWorkspace();
                }
                Console.WriteLine($"PASS: {count} template round-trips; independent WPF windows; sculpt/undo/redo with hair following; project save/load; edited .o export; face .big package reopened at 256 and 512; hair volume and a hair texture change saved in a .big; hair added from a template; likeness window with automatic markers; zoom and 3D navigation; reference move and crop; paint on the head and hair mesh with shared sculpt/paint undo, saved in a .big. Output: {output}");
                app.Shutdown(0);
            }
            catch(Exception ex){Console.Error.WriteLine(ex);app.Shutdown(1);}
        };
        app.Run();
    }
    private static void Layout(Window window,int width,int height){window.Measure(new Size(width,height));window.Arrange(new Rect(0,0,width,height));window.UpdateLayout();}
    private static (int Part,Vector2 Uv)? HitUv(FaceSculptWindow window,Point point)=>((int,Vector2)?)typeof(FaceSculptWindow).GetMethod("HitUv",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,new object[]{point});
    private static void SavePng(BitmapSource image,string path){using var file=File.Create(path);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(file);}
    private static void SaveSideBySide(string path,int width,int height,byte[] left,byte[] right)
    {
        var pixels=new byte[width*2*height*4];
        for(int y=0;y<height;y++){Buffer.BlockCopy(left,y*width*4,pixels,y*width*8,width*4);Buffer.BlockCopy(right,y*width*4,pixels,y*width*8+width*4,width*4);}
        for(int i=3;i<pixels.Length;i+=4)pixels[i]=255; // show colour even where the head's alpha is transparent
        SavePng(BitmapSource.Create(width*2,height,96,96,PixelFormats.Bgra32,null,pixels,width*8),path);
    }
    private static void LayoutContent(Window window,int width,int height){var content=(FrameworkElement)window.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();}
    private static void SaveWindow(Window window,string path,int width,int height)
    {
        var content=(FrameworkElement)window.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);SavePng(bitmap,path);
    }
    private static void Invoke(FaceSculptWindow window,string method,object sender)=>typeof(FaceSculptWindow).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,new[]{sender,new RoutedEventArgs()});
    private static (byte[] Pixels,int Width,int Height) PixelsOf(BitmapSource image)
    {
        var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);var pixels=new byte[converted.PixelWidth*converted.PixelHeight*4];
        converted.CopyPixels(pixels,converted.PixelWidth*4,0);return(pixels,converted.PixelWidth,converted.PixelHeight);
    }
    private static BitmapSource FrontRender(StudioModel model,double yaw=0,double pitch=0)
    {
        var group=new Model3DGroup();foreach(var geometry in model.Geometry)group.Children.Add(geometry.Clone());
        var b=group.Bounds;var c=new Point3D(b.X+b.SizeX/2,b.Y+b.SizeY/2,b.Z+b.SizeZ/2);
        group.Children.Add(new AmbientLight(Color.FromRgb(130,130,130)));group.Children.Add(new DirectionalLight(Colors.White,new Vector3D(-0.2,-0.3,-1)));
        if(yaw!=0||pitch!=0){var turn=new Transform3DGroup();turn.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0,1,0),yaw),c));turn.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1,0,0),pitch),c));foreach(var child in group.Children.OfType<GeometryModel3D>())child.Transform=turn;}
        var viewport=new Viewport3D{Camera=new OrthographicCamera(c+new Vector3D(0,0,60),new Vector3D(0,0,-1),new Vector3D(0,1,0),b.SizeY*1.3),Width=600,Height=700};
        viewport.Children.Add(new ModelVisual3D{Content=group});
        var root=new Grid{Width=600,Height=700,Background=new SolidColorBrush(Color.FromRgb(160,170,180))};root.Children.Add(viewport);
        root.Measure(new Size(600,700));root.Arrange(new Rect(0,0,600,700));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(600,700,96,96,PixelFormats.Pbgra32);bitmap.Render(root);return bitmap;
    }
    private static void SavePreview(StudioModel model,string path)
    {
        var group=new Model3DGroup();foreach(var geometry in model.Geometry)group.Children.Add(geometry.Clone());
        var bounds=group.Bounds;var center=new Point3D(bounds.X+bounds.SizeX/2,bounds.Y+bounds.SizeY/2,bounds.Z+bounds.SizeZ/2);
        double size=Math.Max(bounds.SizeX,Math.Max(bounds.SizeY,bounds.SizeZ));
        group.Children.Add(new AmbientLight(Color.FromRgb(115,115,115)));group.Children.Add(new DirectionalLight(Colors.White,new Vector3D(-1,-1,-2)));
        var viewport=new Viewport3D{Camera=new OrthographicCamera(new Point3D(center.X+size*0.25,center.Y+size*0.05,center.Z+size*2),new Vector3D(-size*0.25,-size*0.05,-size*2),new Vector3D(0,1,0),size*1.45)};
        viewport.Children.Add(new ModelVisual3D{Content=group});
        var background=new Border{Background=new SolidColorBrush(Color.FromRgb(36,43,54)),Child=viewport};
        background.Measure(new Size(300,280));background.Arrange(new Rect(0,0,300,280));background.UpdateLayout();
        var bitmap=new RenderTargetBitmap(300,280,96,96,PixelFormats.Pbgra32);bitmap.Render(background);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var stream=File.Create(path);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);
    }
}
