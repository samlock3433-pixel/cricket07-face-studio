using System.Numerics;
using Quaternion = System.Windows.Media.Media3D.Quaternion;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;

namespace Cricket07.FaceStudio.Views;

public partial class FaceSculptWindow
{
    private Point? _pointerStart;
    private Point _previousPointer;
    private bool _orbit,_pan,_strokeRecorded;
    private MouseButton _navButton;
    private Point? _referenceDragFrom;
    private Vector3[][]? _strokeSource;
    private readonly ModelVisual3D _referencePlane=new();
    private bool _referenceReady,_restoringReference;
    private int _referenceIndex=-1;
    // Per reference (front, side): opacity, scale, x, y, in-front flag, then the crop as left, top, right, bottom (0..1).
    private double[][] _referenceSettings={DefaultReference(),DefaultReference()};
    internal static double[] DefaultReference()=>new[]{0.4,1d,0d,0d,1d,0d,0d,1d,1d};
    private Point3D HeadCenter=>new(_bounds.X+_bounds.SizeX/2,_bounds.Y+_bounds.SizeY/2,_bounds.Z+_bounds.SizeZ/2);
    /// <summary>
    /// Frames the whole head from a preset direction: front, back, left/right (as seen from the front; "side" = right),
    /// threequarter (front-right, slightly above), or fit (keep the current direction).
    /// </summary>
    private void SetView(string view)
    {
        var current=Viewport.Camera is ProjectionCamera c?c.LookDirection:new Vector3D(0,0,-1);
        var direction=view switch
        {
            "back"=>new Vector3D(0,0,1),"left"=>new Vector3D(1,0,0),"side" or "right"=>new Vector3D(-1,0,0),
            "threequarter"=>new Vector3D(-0.62,-0.22,-0.75),"fit"=>current,_=>new Vector3D(0,0,-1),
        };
        direction.Normalize();
        double aspect=Math.Max(1,Viewport.ActualWidth)/Math.Max(1,Viewport.ActualHeight);
        Viewport.Camera=new OrthographicCamera(HeadCenter-direction*_size*3,direction*_size*3,new Vector3D(0,1,0),_size*1.3*Math.Max(1,aspect));
        QueueRedrawPins();
    }
    /// <summary>Frames the clicked feature closely, keeping the view direction.</summary>
    private void FocusFeature()
    {
        if(_center is not Vector3 c||Viewport.Camera is not OrthographicCamera camera){Status.Text="Click a feature on the head first, then Focus frames it.";return;}
        var direction=camera.LookDirection;direction.Normalize();
        camera.Position=new Point3D(c.X,c.Y,c.Z)-direction*_size*3;camera.LookDirection=direction*_size*3;camera.Width=_size*0.45;
        QueueRedrawPins();
    }
    /// <summary>Zooms by <paramref name="factor"/> keeping the point under <paramref name="at"/> (viewport pixels) in place.</summary>
    private void ZoomAt(Point at,double factor)
    {
        if(Viewport.Camera is not OrthographicCamera camera)return;
        double before=PixelScale,width=Math.Clamp(camera.Width/factor,_size*0.05,_size*20),after=width/Math.Max(1,Viewport.ActualWidth);
        var axes=CameraAxes();
        camera.Position+=(axes.Right*(at.X-Viewport.ActualWidth/2)+axes.Up*(Viewport.ActualHeight/2-at.Y))*(before-after);
        camera.Width=width;
        QueueRedrawPins();
    }
    private Point ViewportCentre=>new(Viewport.ActualWidth/2,Viewport.ActualHeight/2);
    private const double OrbitStep=15/0.35; // pixels of drag that orbit 15 degrees
    private void OnNavigate(object sender,RoutedEventArgs e)
    {
        switch((string)((Button)sender).Tag)
        {
            case "orbit-left":Smoothly(()=>Navigate(new System.Windows.Vector(-OrbitStep,0),false));break;
            case "orbit-right":Smoothly(()=>Navigate(new System.Windows.Vector(OrbitStep,0),false));break;
            case "orbit-up":Smoothly(()=>Navigate(new System.Windows.Vector(0,-OrbitStep),false));break;
            case "orbit-down":Smoothly(()=>Navigate(new System.Windows.Vector(0,OrbitStep),false));break;
            case "zoom-in":Smoothly(()=>ZoomAt(ViewportCentre,1.25));break;
            case "zoom-out":Smoothly(()=>ZoomAt(ViewportCentre,0.8));break;
            case "focus":Smoothly(FocusFeature);break;
            case var view:Smoothly(()=>SetView(view),360);break;
        }
    }
    /// <summary>Keyboard navigation while the 3D view has focus; Ctrl+Z / Ctrl+Y anywhere in the window.</summary>
    private void OnKey(object sender,KeyEventArgs e)
    {
        var modifiers=Keyboard.Modifiers;
        if(modifiers==ModifierKeys.Control&&e.Key is Key.Z or Key.Y){if(e.Key==Key.Z)OnUndo(this,e);else OnRedo(this,e);e.Handled=true;return;}
        if(e.Key==Key.Delete&&HeadPins&&_selectedPin>=0){RemovePin(_selectedPin);e.Handled=true;return;}
        // Alt with Paint: eyedropper (as in paint programs). Alt + left drag still orbits; that puts the old colour back.
        if(e.Key==Key.System&&e.SystemKey is Key.LeftAlt or Key.RightAlt)
        {
            if(!e.IsRepeat&&HeadPaintTool==Core.PaintTool.Paint&&_paint is null&&Viewport.IsMouseOver)
            {var before=_headColour;if(PickUnderCursor(Mouse.GetPosition(Viewport.Viewport)))_altSampled=before;}
            e.Handled=true;return; // no window-menu focus on Alt
        }
        // W: wireframe, V: single-vertex mode (not while typing in a box).
        if(modifiers==ModifierKeys.None&&e.Key is Key.W or Key.V&&!(Keyboard.FocusedElement is TextBox))
        {var box=e.Key==Key.W?ShowWireframe:VertexMode;box.IsChecked=box.IsChecked!=true;if(e.Key==Key.W)OnWireframe(box,e);else OnVertexMode(box,e);e.Handled=true;return;}
        // [ and ]: smaller / larger brush, as in paint programs.
        if(modifiers==ModifierKeys.None&&e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets&&HeadPaintTool is not null)
        {HeadBrushSize.Value=Math.Clamp(HeadBrushSize.Value*(e.Key==Key.OemCloseBrackets?1.15:1/1.15),HeadBrushSize.Minimum,HeadBrushSize.Maximum);e.Handled=true;return;}
        // I: eyedropper, the colour under the cursor (as in Photoshop), wherever keyboard focus is.
        if(e.Key==Key.I&&modifiers==ModifierKeys.None&&Viewport.IsMouseOver&&!(Keyboard.FocusedElement is TextBox)&&PickUnderCursor(Mouse.GetPosition(Viewport.Viewport))){e.Handled=true;return;}
        if(!Viewport.IsKeyboardFocusWithin)return;
        bool shift=modifiers.HasFlag(ModifierKeys.Shift);
        System.Windows.Vector? step=e.Key switch{Key.Left=>new(-1,0),Key.Right=>new(1,0),Key.Up=>new(0,-1),Key.Down=>new(0,1),_=>null};
        if(step is { } s){Smoothly(()=>Navigate(shift?s*40:s*OrbitStep,shift),180);e.Handled=true;return;}
        switch(e.Key)
        {
            case Key.Add or Key.OemPlus:Smoothly(()=>ZoomAt(ViewportCentre,1.25));break;
            case Key.Subtract or Key.OemMinus:Smoothly(()=>ZoomAt(ViewportCentre,0.8));break;
            case Key.F:Smoothly(()=>SetView("fit"),360);break;
            case Key.Home:Smoothly(()=>SetView("front"),360);break;
            default:return;
        }
        e.Handled=true;
    }
    private (Vector3D Right,Vector3D Up) CameraAxes()
    {
        var camera=Viewport.Camera;var forward=camera.LookDirection;forward.Normalize();
        var right=Vector3D.CrossProduct(forward,camera.UpDirection);right.Normalize();
        var up=Vector3D.CrossProduct(right,forward);up.Normalize();return(right,up);
    }
    private double PixelScale=>Viewport.Camera is OrthographicCamera camera?camera.Width/Math.Max(1,Viewport.ActualWidth):_size/Math.Max(1,Viewport.ActualHeight);
    private void OnPointerDown(object sender,MouseButtonEventArgs e)
    {
        if(_exporting)return;
        Viewport.Focus();StopCamera(); // a drag takes over from a running view move
        if(ShapePins.IsChecked==true&&e.ChangedButton==MouseButton.Right&&NearestShapePin(e.GetPosition(Viewport)) is int pin&&pin>=0)
        {_selectedShapePin=pin;OnRemoveShapePin(sender,e);e.Handled=true;return;}
        // Orbit: right or middle drag, or Alt + left drag (touchpads). Hold Shift to pan instead.
        bool altLeft=e.ChangedButton==MouseButton.Left&&Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if(e.ChangedButton is MouseButton.Middle or MouseButton.Right||altLeft)
        {
            if(altLeft&&_altSampled is { } before){SetHeadColour(before);_altSampled=null;Status.Text="Orbiting (the colour Alt sampled was put back).";}
            _orbit=true;_navButton=e.ChangedButton;_pan=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);_previousPointer=e.GetPosition(Viewport);
            Viewport.CaptureMouse();e.Handled=true;return;
        }
        if(e.ChangedButton!=MouseButton.Left)return;
        // Double-click the reference photo (or anywhere while moving it) to crop it.
        if(e.ClickCount==2&&_referenceIndex>=0&&_referencePlane.Content is not null&&(ReferenceEdit.IsChecked==true||HitsReference(e.GetPosition(Viewport))))
        {EndPointer();Viewport.ReleaseMouseCapture();OpenCrop(_referenceIndex);e.Handled=true;return;}
        // "Move reference only": left drag moves the photo; the head is not picked or sculpted.
        if(ReferenceEdit.IsChecked==true)
        {
            if(_referencePlane.Content is null){Status.Text="Choose Front image or Side image (and load the photo) to move it.";e.Handled=true;return;}
            _referenceDragFrom=e.GetPosition(Viewport);Viewport.CaptureMouse();e.Handled=true;return;
        }
        if(ShapePins.IsChecked==true)
        {
            if(BeginShapeDrag(e.GetPosition(Viewport)))Viewport.CaptureMouse();
            e.Handled=true;return;
        }
        // Pin warp: grab a pin or a painted feature and drag it into place.
        if(HeadPins)
        {
            if(BeginPin(e.GetPosition(Viewport.Viewport)))Viewport.CaptureMouse();
            e.Handled=true;return;
        }
        // A texture paint tool: left drag paints the texture under the cursor instead of sculpting.
        if(HeadPaintTool is not null)
        {
            if(BeginPaint(e.GetPosition(Viewport.Viewport),Keyboard.Modifiers.HasFlag(ModifierKeys.Control))&&_paint is not null)Viewport.CaptureMouse();
            e.Handled=true;return;
        }
        _center=null;OnPick(sender,e);
        if(_center is null)return;
        _grabVertices=VertexMode.IsChecked==true?PickVertices(e.GetPosition(Viewport.Viewport)):null; // single vertex instead of the soft brush
        if(VertexMode.IsChecked==true&&_grabVertices is null)return;
        _pointerStart=e.GetPosition(Viewport);_strokeSource=AllPositions();_strokeRecorded=false;Viewport.CaptureMouse();e.Handled=true;
    }
    private void OnPointerMove(object sender,MouseEventArgs e)
    {
        var point=e.GetPosition(Viewport);
        UpdateBrushRing(point); // brush ring follows the cursor (hidden unless a paint tool is chosen)
        UpdateVertexMarkers(point);
        if(_orbit)
        {
            Navigate(point-_previousPointer,_pan);_previousPointer=point;
            e.Handled=true;return;
        }
        if(_referenceDragFrom is Point from){MoveReference(point-from);_referenceDragFrom=point;e.Handled=true;return;}
        if(_shapeDrag>=0){DragShapePin(point);e.Handled=true;return;}
        if(_paint is not null){ContinuePaint(e.GetPosition(Viewport.Viewport));e.Handled=true;return;}
        if(_pinDragging){DragPin(e.GetPosition(Viewport.Viewport));e.Handled=true;return;}
        if(_pointerStart is not Point start||_strokeSource is null||_center is not Vector3 center)return;
        var movement=point-start;if(movement.Length<2&&!_strokeRecorded)return;
        if(!_strokeRecorded){_undo.Push(new UndoStep(Snapshot()));_redo.Clear();_strokeRecorded=true;}
        var basis=CameraAxes();var world=(basis.Right*movement.X-basis.Up*movement.Y)*PixelScale;
        var displacement=new Vector3((float)world.X,(float)world.Y,(float)world.Z);
        if(_grabVertices is { } vertices){MoveVertices(vertices,displacement);e.Handled=true;Status.Text="Moving one vertex: release to finish. Undo restores it.";return;}
        for(int p=0;p<_parts.Count;p++)
            if(!Locked(p))SetPositions(p,Core.SculptBrush.Move(_strokeSource[p],center,displacement,(float)(_size*Radius.Value/100),Plane,Floor).Select(v=>new Point3D(v.X,v.Y,v.Z)));
        e.Handled=true;
        Status.Text="Grab sculpt: release to finish. Undo restores the whole stroke.";
    }
    private void Navigate(System.Windows.Vector delta,bool pan)
    {
        var camera=Viewport.Camera;var axes=CameraAxes();
            if(pan)camera.Position+=(-axes.Right*delta.X+axes.Up*delta.Y)*PixelScale;
            else
            {
                var target=camera.Position+camera.LookDirection;
                var yaw=new Quaternion(new Vector3D(0,1,0),-delta.X*0.35);
                var pitch=new Quaternion(axes.Right,-delta.Y*0.35);
                var matrix=Matrix3D.Identity;matrix.Rotate(yaw*pitch);
                var look=matrix.Transform(camera.LookDirection);var normalized=look;normalized.Normalize();
                if(Math.Abs(normalized.Y)<0.985){camera.LookDirection=look;camera.Position=target-look;camera.UpDirection=new Vector3D(0,1,0);}
            }
            QueueRedrawPins();
    }
    private void OnPointerUp(object sender,MouseButtonEventArgs e)
    {
        if((_orbit&&e.ChangedButton==_navButton)||((_pointerStart is not null||_referenceDragFrom is not null||_paint is not null||_pinDragging||_shapeDrag>=0)&&e.ChangedButton==MouseButton.Left))
        {EndPointer();Viewport.ReleaseMouseCapture();e.Handled=true;}
    }
    private void EndPointer(){EndShapeDrag();_grabVertices=null;_orbit=false;_pointerStart=null;_strokeSource=null;_referenceDragFrom=null;FinishPaint();EndPin();}
    /// <summary>Moves the reference photo by a drag in viewport pixels, in its own plane, so it follows the cursor.</summary>
    private void MoveReference(System.Windows.Vector delta)
    {
        if(_referenceIndex<0)return;
        var axes=CameraAxes();var world=(axes.Right*delta.X-axes.Up*delta.Y)*PixelScale;
        var planeRight=_referenceIndex==0?new Vector3D(1,0,0):new Vector3D(0,0,-1);
        var s=_referenceSettings[_referenceIndex];
        s[2]=Math.Clamp(s[2]+Vector3D.DotProduct(world,planeRight)/_size,-1,1);
        s[3]=Math.Clamp(s[3]+world.Y/_size,-1,1);
        UpdateReference();_dirty=true;
        Status.Text="Moving the reference photo. Untick 'Move reference only' to sculpt again.";
    }
    private void OnReferenceEdit(object sender,RoutedEventArgs e)
    {
        bool on=ReferenceEdit.IsChecked==true;
        if(on)HeadTool.SelectedIndex=0; // moving the photo and painting both use left drag
        Viewport.Cursor=on?Cursors.SizeAll:null;
        Status.Text=on?"Left drag in the 3D view moves the reference photo; the head is not selected or sculpted.":"Sculpting again: click a feature on the head.";
        SyncTools();
    }
    private void OnCaptureLost(object sender,MouseEventArgs e)=>EndPointer();
    private void OnWheel(object sender,MouseWheelEventArgs e)
    {
        var at=e.GetPosition(Viewport);
        Smoothly(()=>ZoomAt(at,Math.Pow(1.18,e.Delta/120d)),150); // touchpad pinch arrives as Ctrl+wheel: same zoom
        e.Handled=true;
    }
    private void OnProportion(object sender,RoutedEventArgs e)
    {
        if(_center is not Vector3 center){Status.Text="Click the feature center before changing its proportions.";return;}
        Edit(p=>Core.SculptBrush.Proportion(Positions(p),center,new Vector3((float)FeatureWidth.Value,(float)FeatureHeight.Value,(float)FeatureDepth.Value),(float)(_size*Radius.Value/100),Plane,Floor));
        FeatureWidth.Value=FeatureHeight.Value=FeatureDepth.Value=1;
    }
    private void OnReferenceMode(object sender,SelectionChangedEventArgs? e)
    {
        if(!_referenceReady)return;
        _referenceIndex=ReferenceMode.SelectedIndex-1;
        if(_referenceIndex>=0)
        {
            _restoringReference=true;var settings=_referenceSettings[_referenceIndex];
            ReferenceOpacity.Value=settings[0];ReferenceScale.Value=settings[1];ReferenceOverlay.IsChecked=settings[4]==1;
            _restoringReference=false;SetView(_referenceIndex==0?"front":"side");
        }
        UpdateReference();_dirty=true;
    }
    private void OnReferenceOption(object sender,RoutedEventArgs e)=>ReferenceChanged();
    private void OnReferenceSetting(object sender,RoutedPropertyChangedEventArgs<double> e)=>ReferenceChanged();
    private void ReferenceChanged()
    {
        if(!_referenceReady||_restoringReference)return;
        // Position (2, 3) is set by dragging the photo; the sliders own opacity, scale and overlay.
        if(_referenceIndex>=0){var s=_referenceSettings[_referenceIndex];_referenceSettings[_referenceIndex]=new[]{ReferenceOpacity.Value,ReferenceScale.Value,s[2],s[3],ReferenceOverlay.IsChecked==true?1d:0d,s[5],s[6],s[7],s[8]};}
        UpdateReference();_dirty=true;
    }
    /// <summary>Opens the crop window for a reference (0 front, 1 side) and applies the chosen area.</summary>
    private void OpenCrop(int index)
    {
        if((index==0?FrontReference.Source:SideReference.Source) is not BitmapSource image){Status.Text="Load the reference photo first.";return;}
        var s=_referenceSettings[index];
        var window=new CropWindow(image,new Rect(s[5],s[6],s[7]-s[5],s[8]-s[6])){Owner=this};
        if(window.ShowDialog()!=true||window.Result is not { } crop)return;
        (s[5],s[6],s[7],s[8])=(crop.Left,crop.Top,crop.Right,crop.Bottom);
        UpdateReference();_dirty=true;
        Status.Text=crop.Width>=0.999&&crop.Height>=0.999?"Showing the whole reference photo.":"Reference cropped. The rest is only hidden: double-click the photo to change or reset the crop.";
    }
    private bool HitsReference(Point point)
    {
        if(_referencePlane.Content is not GeometryModel3D plane)return false;
        bool hit=false;
        VisualTreeHelper.HitTest(Viewport.Viewport,null,r=>{if(r is RayMeshGeometry3DHitTestResult h&&h.MeshHit==plane.Geometry){hit=true;return HitTestResultBehavior.Stop;}return HitTestResultBehavior.Continue;},new PointHitTestParameters(point));
        return hit;
    }
    private void OnReferenceThumbnail(object sender,MouseButtonEventArgs e)
    {
        if(e.ClickCount==2){OpenCrop(ReferenceEquals(sender,FrontReference)?0:1);e.Handled=true;}
    }
    private void UpdateReference()
    {
        _referencePlane.Content=null;
        if(!_referenceReady||ReferenceMode.SelectedIndex==0)return;
        var image=_referenceIndex==0?FrontReference.Source:SideReference.Source;if(image is null)return;
        var right=_referenceIndex==0?new Vector3D(1,0,0):new Vector3D(0,0,-1);
        var outward=_referenceIndex==0?new Vector3D(0,0,1):new Vector3D(1,0,0);
        double height=_size*ReferenceScale.Value,width=height*image.Width/image.Height; // the whole photo
        var s=_referenceSettings[_referenceIndex];
        var photoCentre=HeadCenter+right*(_size*s[2])+new Vector3D(0,_size*s[3],0)+outward*(_size*(ReferenceOverlay.IsChecked==true?0.7:-0.7));
        // Non-destructive crop: draw only the kept part, exactly where it sits in the whole photo, so alignment does not jump.
        double l=s[5],t=s[6],r=s[7],b=s[8];
        var center=photoCentre+right*(width*((l+r)/2-0.5))+new Vector3D(0,height*(0.5-(t+b)/2),0);
        var horizontal=right*width*(r-l)/2;var vertical=new Vector3D(0,height*(b-t)/2,0);
        var mesh=new MeshGeometry3D{Positions=new Point3DCollection{center-horizontal-vertical,center+horizontal-vertical,center+horizontal+vertical,center-horizontal+vertical},TriangleIndices=new Int32Collection{0,1,2,0,2,3},TextureCoordinates=new PointCollection{new(l,b),new(r,b),new(r,t),new(l,t)}};
        // WPF draws emissive light additively, so the photo alone would brighten whatever is behind it instead of
        // covering it. A black diffuse layer at the same opacity first darkens the background by (1 - opacity);
        // black stays black under any light, so the sum is a true blend: background × (1 - a) + photo × a.
        double alpha=ReferenceOpacity.Value;
        var material=new MaterialGroup{Children={new DiffuseMaterial(new SolidColorBrush(Colors.Black){Opacity=alpha}),new EmissiveMaterial(new ImageBrush(image){Opacity=alpha,ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,1,1)})}}; // absolute UVs, or WPF would stretch the crop back over the whole brush
        _referencePlane.Content=new GeometryModel3D(mesh,material){BackMaterial=material};
    }
}


