using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickPreview;

internal static class Program
{
    private static int _exitCode;
    [STAThread]
    private static int Main(string[] arguments)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunAsync(arguments); }
            catch (Exception exception) { Console.Error.WriteLine(exception); _exitCode = 1; }
            finally { application.Shutdown(); Dispatcher.ExitAllFrames(); }
        }));
        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task RunAsync(string[] arguments)
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures");
        CreateFixtures(fixtureDirectory);
        var screenshotDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../screenshots"));
        Directory.CreateDirectory(screenshotDirectory);
        CoreWebView2Environment.SetLoaderDllFolderPath(AppContext.BaseDirectory);
        var browser = new WebView2();
        var window = new Window { Content = browser, Width = 1000, Height = 700, ShowActivated = false, Title = "QuickPreview 3D smoke tests" };
        window.Show();
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppContext.BaseDirectory, "WebView2-test"));
        await browser.EnsureCoreWebView2Async(environment);
        browser.CoreWebView2.ProcessFailed += (_, e) => Console.WriteLine("PROCESS FAILED: " + e.ProcessFailedKind);
        await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) => Console.WriteLine("JS ERROR: " + e.ParameterObjectAsJson);

        await VerifyInitialModelFramingAsync(browser, window, fixtureDirectory, screenshotDirectory, arguments);
        if (arguments.Contains("--framing-only"))
        {
            browser.Dispose();
            window.Close();
            Console.WriteLine("PASS: Initial STL/STEP/3MF/OBJ framing; screenshots: " + screenshotDirectory);
            return;
        }

        foreach (var theme in new[] { "light", "dark" })
        foreach (var extension in new[] { "stl", "obj", "3mf", "step", "ifc", "3dm" })
        {
            var path = Path.Combine(fixtureDirectory, "cube." + extension);
            var originalHash = SHA256.HashData(File.ReadAllBytes(path));
            using var session = await ModelPreviewService.CreateSessionAsync(path);
            Assert(session.ModelFiles.Keys.All(name => !name.EndsWith(".txt")), "Unrelated documents not exposed");
            session.Attach(browser.CoreWebView2, lightTheme: theme == "light");
            ModelPreviewInfo result;
            try { result = await session.Ready.WaitAsync(TimeSpan.FromSeconds(45)); }
            catch
            {
                try { Console.WriteLine(await browser.CoreWebView2.ExecuteScriptAsync("document.body.innerText")); } catch(Exception error) { Console.WriteLine("BODY ERROR: " + error.Message); }
                throw;
            }
            Assert(result.TriangleCount == 12 && result.MeshCount >= 1, extension.ToUpperInvariant() + " true cube geometry (12 triangles)");
            Assert(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Model file unmodified");
            await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('edges').click(); document.getElementById('fit').click();");
            var pressed = await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('edges').getAttribute('aria-pressed')");
            Assert(pressed == "\"true\"", "Working edges control");
            // ExecuteScriptAsync doesn't await promises. Use a synchronous property after settling.
            await browser.CoreWebView2.ExecuteScriptAsync("fetch('https://example.org/never-send-model').then(()=>window.externalBlocked=false).catch(()=>window.externalBlocked=true)");
            await Task.Delay(100);
            Assert(await browser.CoreWebView2.ExecuteScriptAsync("window.externalBlocked") == "true", "Outbound network blocked");
            foreach (var width in extension == "step" ? new[] { 1000d, 520d } : new[] { 1000d })
            {
                window.Width = width;
                window.UpdateLayout();
                await Task.Delay(150);
                using var alignment = JsonDocument.Parse(await browser.CoreWebView2.ExecuteScriptAsync("(()=>{const a=document.getElementById('fit').getBoundingClientRect(),b=document.getElementById('edges').getBoundingClientRect();return {top:Math.abs(a.top-b.top),height:Math.abs(a.height-b.height),overflow:document.documentElement.scrollWidth>innerWidth};})()"));
                Assert(alignment.RootElement.GetProperty("top").GetDouble() <= 1 && alignment.RootElement.GetProperty("height").GetDouble() <= 1 && !alignment.RootElement.GetProperty("overflow").GetBoolean(), "3D controls aligned, no page overflow");
                await AssertFittedGeometryAsync(browser, $"{extension} {theme} {width:0}px");
                using var screenshot = File.Create(Path.Combine(screenshotDirectory, $"{extension}-{theme}-{width:0}.png"));
                await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                if (extension == "step" && width == 520)
                {
                    await browser.CoreWebView2.ExecuteScriptAsync("document.documentElement.style.zoom='1.25'");
                    await Task.Delay(100);
                    Assert(await browser.CoreWebView2.ExecuteScriptAsync("(()=>{const a=document.getElementById('fit').getBoundingClientRect(),b=document.getElementById('edges').getBoundingClientRect();return Math.abs(a.top-b.top)<=1&&Math.abs(a.height-b.height)<=1&&document.documentElement.scrollWidth<=innerWidth;})()") == "true", "3D controls aligned at125% text scale");
                    await AssertFittedGeometryAsync(browser, $"STEP {theme} 520px 125%");
                    using var enlarged = File.Create(Path.Combine(screenshotDirectory,$"step-{theme}-520-125percent.png"));
                    await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,enlarged);
                    await AssertManualCameraSurvivesResizeAsync(browser, window);
                }
            }
            Console.WriteLine($"PASS: {extension} {theme}: {result.Summary}");
        }
        browser.Dispose();
        window.Close();
        Console.WriteLine("PASS: Offline model rendering; screenshots: " + screenshotDirectory);
    }

    private static async Task VerifyInitialModelFramingAsync(WebView2 browser, Window window, string fixtures, string screenshots, string[] arguments)
    {
        var models = new List<(string Path, string Label)>
        {
            (Path.Combine(fixtures, "initial-z-up-slab.stl"), "z-up-slab"),
            (Path.Combine(fixtures, "initial-negative-offset.stl"), "negative-offset"),
            (Path.Combine(fixtures, "initial-large-offset.stl"), "large-offset"),
            (Path.Combine(fixtures, "initial-offset.step"), "offset-step"),
            (Path.Combine(fixtures, "initial-offset.obj"), "offset-obj"),
            (Path.Combine(fixtures, "initial-transformed.3mf"), "transformed-3mf"),
            (Path.Combine(fixtures, "initial-millimetres-in-metres.obj"), "tiny-units"),
            (Path.Combine(fixtures, "initial-large-world-units.obj"), "huge-units")
        };
        var external = Array.IndexOf(arguments, "--model");
        if (external >= 0)
        {
            if (external + 1 >= arguments.Length) throw new ArgumentException("--model requires a local model path.");
            models.Add((Path.GetFullPath(arguments[external + 1]), "external-model"));
        }
        foreach (var (path, label) in models)
        foreach (var width in new[] { 1000d, 520d })
        {
            // Stabilize the host before loading; neither resizing nor pressing Fit
            // may repair the first automatically chosen camera in this regression.
            window.Width = width; window.Height = 700;
            window.UpdateLayout();
            await Task.Delay(100);
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            using var session = await ModelPreviewService.CreateSessionAsync(path);
            session.Attach(browser.CoreWebView2, lightTheme: true);
            await session.Ready.WaitAsync(TimeSpan.FromSeconds(45));
            await SettleFramesAsync(browser);
            var json = await browser.CoreWebView2.ExecuteScriptAsync(InitialFramingMeasurements);
            File.WriteAllText(Path.Combine(screenshots, $"initial-{label}-{width:0}.json"), json);
            using (var screenshot = File.Create(Path.Combine(screenshots, $"initial-{label}-{width:0}.png")))
                await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
            using var measurements = JsonDocument.Parse(json);
            var value = measurements.RootElement;
            Console.WriteLine($"Initial framing {label} {width:0}px: {json}");
            Assert(value.GetProperty("finite").GetBoolean() && value.GetProperty("vertices").GetInt32() > 0,
                "Initial model projection contains finite rendered vertices: " + label);
            Assert(Math.Abs(value.GetProperty("centerX").GetDouble()) < 0.02 && Math.Abs(value.GetProperty("centerY").GetDouble()) < 0.02,
                "Initial camera centers transformed model before Fit: " + label + " " + width);
            Assert(value.GetProperty("targetError").GetDouble() < 0.00001,
                "Initial orbit target matches transformed bounds center: " + label);
            Assert(value.GetProperty("minX").GetDouble() > -0.99 && value.GetProperty("maxX").GetDouble() < 0.99 &&
                   value.GetProperty("minY").GetDouble() > -0.99 && value.GetProperty("maxY").GetDouble() < 0.99 &&
                   value.GetProperty("minZ").GetDouble() >= -1 && value.GetProperty("maxZ").GetDouble() <= 1,
                "Initial model is entirely inside viewport before Fit: " + label + " " + width);
            Assert(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Initial framing leaves model unmodified: " + label);
            if (width == 1000 && label is "tiny-units" or "huge-units")
                await AssertClippingSurvivesManualZoomAsync(browser, label);
        }
    }

    private static async Task AssertClippingSurvivesManualZoomAsync(WebView2 browser, string label)
    {
        await browser.CoreWebView2.ExecuteScriptAsync("window.unitCamera=window.quickPreviewModel.viewer.GetViewer().GetCamera().Clone();document.getElementById('viewport').dispatchEvent(new Event('pointerdown'))");
        foreach (var multiplier in new[] { 0.8, 12d })
        {
            var factor = multiplier.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await browser.CoreWebView2.ExecuteScriptAsync($"(()=>{{const n=window.quickPreviewModel.viewer.GetViewer(),c=window.unitCamera.Clone();for(const axis of ['x','y','z'])c.eye[axis]=c.center[axis]+(c.eye[axis]-c.center[axis])*{factor};n.SetCamera(c);}})()");
            await SettleFramesAsync(browser);
            using var measurements = JsonDocument.Parse(await browser.CoreWebView2.ExecuteScriptAsync(InitialFramingMeasurements));
            Assert(measurements.RootElement.GetProperty("finite").GetBoolean() &&
                   measurements.RootElement.GetProperty("minZ").GetDouble() >= -1 &&
                   measurements.RootElement.GetProperty("maxZ").GetDouble() <= 1,
                $"Manual zoom keeps model depth inside the clipping planes: {label} ×{multiplier}");
            Assert(await browser.CoreWebView2.ExecuteScriptAsync("(()=>{const c=window.quickPreviewModel.viewer.GetViewer().camera;return Number.isFinite(c.near)&&Number.isFinite(c.far)&&c.near>0&&c.far>c.near})()") == "true",
                "Clipping planes remain finite and positive: " + label);
        }
    }

    private const string InitialFramingMeasurements = @"(()=>{
      const n=window.quickPreviewModel.viewer.GetViewer(),b=n.GetBoundingBox(()=>true),center=b.min.clone().add(b.max).multiplyScalar(.5),projected=center.clone().project(n.camera),target=n.GetCamera().center;
      let minX=Infinity,maxX=-Infinity,minY=Infinity,maxY=-Infinity,minZ=Infinity,maxZ=-Infinity,vertices=0,finite=true;
      n.mainModel.EnumerateMeshes(mesh=>{const a=mesh.geometry.getAttribute('position'),p=b.min.clone();for(let i=0;i<a.count;i++){
        p.fromBufferAttribute(a,i).applyMatrix4(mesh.matrixWorld).project(n.camera);vertices++;
        finite=finite&&Number.isFinite(p.x)&&Number.isFinite(p.y)&&Number.isFinite(p.z);
        minX=Math.min(minX,p.x);maxX=Math.max(maxX,p.x);minY=Math.min(minY,p.y);maxY=Math.max(maxY,p.y);minZ=Math.min(minZ,p.z);maxZ=Math.max(maxZ,p.z);
      }});
      return {vertices,finite,centerX:projected.x,centerY:projected.y,targetError:Math.hypot(target.x-center.x,target.y-center.y,target.z-center.z)/Math.max(1,b.min.distanceTo(b.max)),
        minX,maxX,minY,maxY,minZ,maxZ,worldMin:b.min,worldMax:b.max,target,canvas:n.GetCanvasSize()};
    })()";

    private static async Task SettleFramesAsync(WebView2 browser)
    {
        // ExecuteScriptAsync does not await a returned Promise. Poll a marker set
        // by three browser frames so the actual Three camera/matrices are current.
        await browser.CoreWebView2.ExecuteScriptAsync("window.initialFramingSettled=false;requestAnimationFrame(()=>requestAnimationFrame(()=>requestAnimationFrame(()=>window.initialFramingSettled=true)))");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (await browser.CoreWebView2.ExecuteScriptAsync("window.initialFramingSettled") != "true")
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Model browser frames did not settle.");
            await Task.Delay(15);
        }
    }

    private static async Task AssertFittedGeometryAsync(WebView2 browser, string description)
    {
        // Project all eight corners of the actual world-space model bounds through
        // Three's current render camera. A fitted cube must remain wholly inside the
        // visible frustum; checking only toolbar/page overflow missed this regression.
        const string script = "(()=>{const n=window.quickPreviewModel.viewer.GetViewer(),b=n.GetBoundingBox(()=>true);let x=0,y=0,z=0;for(const px of [b.min.x,b.max.x])for(const py of [b.min.y,b.max.y])for(const pz of [b.min.z,b.max.z]){const p=b.min.clone().set(px,py,pz).project(n.camera);x=Math.max(x,Math.abs(p.x));y=Math.max(y,Math.abs(p.y));z=Math.max(z,Math.abs(p.z));}return {x,y,z};})()";
        using var json = JsonDocument.Parse(await browser.CoreWebView2.ExecuteScriptAsync(script));
        var bounds = json.RootElement;
        var x = bounds.GetProperty("x").GetDouble();
        var y = bounds.GetProperty("y").GetDouble();
        var z = bounds.GetProperty("z").GetDouble();
        Console.WriteLine($"Projected cube bounds {description}: |x|={x:F3}, |y|={y:F3}, |z|={z:F3}");
        Assert(double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) &&
               x < 0.99 && y < 0.99 && z <= 1, "Entire fitted model inside viewport: " + description);
    }

    private static async Task AssertManualCameraSurvivesResizeAsync(WebView2 browser, Window window)
    {
        const string manualZoom = "(()=>{const n=window.quickPreviewModel.viewer.GetViewer(),distance=()=>{const c=n.GetCamera();return Math.hypot(c.eye.x-c.center.x,c.eye.y-c.center.y,c.eye.z-c.center.z);};const before=distance();document.querySelector('#viewport canvas').dispatchEvent(new WheelEvent('wheel',{deltaY:-240,bubbles:true,cancelable:true}));window.manualDistance=distance();return Math.abs(window.manualDistance-before)>0.001;})()";
        Assert(await browser.CoreWebView2.ExecuteScriptAsync(manualZoom) == "true", "Mouse wheel deliberately changes camera zoom");
        window.Width = 580;
        window.UpdateLayout();
        await Task.Delay(150);
        const string retained = "(()=>{const c=window.quickPreviewModel.viewer.GetViewer().GetCamera(),d=Math.hypot(c.eye.x-c.center.x,c.eye.y-c.center.y,c.eye.z-c.center.z);return Math.abs(d-window.manualDistance)<0.000001;})()";
        Assert(await browser.CoreWebView2.ExecuteScriptAsync(retained) == "true", "Window resize preserves deliberate user zoom");
        window.Width = 520;
        window.UpdateLayout();
        await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('fit').click()");
        await Task.Delay(150);
        await AssertFittedGeometryAsync(browser, "Fit restores automatic framing after manual zoom");
    }

    private static void CreateFixtures(string directory)
    {
        Directory.CreateDirectory(directory);
        WriteBinarySlab(Path.Combine(directory, "initial-z-up-slab.stl"), 0, 0, 0);
        WriteBinarySlab(Path.Combine(directory, "initial-negative-offset.stl"), -420, -80, -310);
        WriteBinarySlab(Path.Combine(directory, "initial-large-offset.stl"), 1_000_000, -2_000_000, 3_000_000);
        WriteUnitCubeObj(Path.Combine(directory, "initial-millimetres-in-metres.obj"), 0.001);
        WriteUnitCubeObj(Path.Combine(directory, "initial-large-world-units.obj"), 1_000_000);
        File.WriteAllText(Path.Combine(directory, "private.txt"), "not a model dependency");
        var vertices = new[] { (0,0,0),(20,0,0),(20,20,0),(0,20,0),(0,0,20),(20,0,20),(20,20,20),(0,20,20) };
        var faces = new[] { new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,1,5,4},new[]{1,2,6,5},new[]{2,3,7,6},new[]{3,0,4,7} };
        var obj = new StringBuilder("# Original QuickPreview cube fixture\n");
        foreach(var (x,y,z) in vertices) obj.AppendLine($"v {x} {y} {z}");
        foreach(var face in faces) obj.AppendLine("f " + string.Join(' ', face.Select(i=>i+1)));
        File.WriteAllText(Path.Combine(directory,"cube.obj"), obj.ToString());
        var stl = new StringBuilder("solid QuickPreviewCube\n");
        foreach(var face in faces)
        foreach(var triangle in new[]{new[]{face[0],face[1],face[2]},new[]{face[0],face[2],face[3]}})
        {
            stl.AppendLine("facet normal 0 0 0\nouter loop");
            foreach(var index in triangle) { var (x,y,z)=vertices[index]; stl.AppendLine($"vertex {x} {y} {z}"); }
            stl.AppendLine("endloop\nendfacet");
        }
        stl.AppendLine("endsolid QuickPreviewCube");
        File.WriteAllText(Path.Combine(directory,"cube.stl"),stl.ToString());
        using (var file=File.Create(Path.Combine(directory,"cube.3mf")))
        using (var zip=new ZipArchive(file,ZipArchiveMode.Create))
        {
            Write("[Content_Types].xml","<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");
            Write("_rels/.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>");
            var xml=new StringBuilder("<model unit=\"millimeter\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\"><resources><object id=\"1\" type=\"model\"><mesh><vertices>");
            foreach(var(x,y,z) in vertices) xml.Append($"<vertex x=\"{x}\" y=\"{y}\" z=\"{z}\"/>");
            xml.Append("</vertices><triangles>");
            foreach(var face in faces) { xml.Append($"<triangle v1=\"{face[0]}\" v2=\"{face[1]}\" v3=\"{face[2]}\"/><triangle v1=\"{face[0]}\" v2=\"{face[2]}\" v3=\"{face[3]}\"/>"); }
            xml.Append("</triangles></mesh></object></resources><build><item objectid=\"1\"/></build></model>");
            Write("3D/3dmodel.model",xml.ToString());
            void Write(string name,string content){using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write(content);}
        }
        var step=new StringBuilder("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('Original QuickPreview cube fixture'),'2;1');\nFILE_NAME('cube.step','2026-11-16T00:00:00',('QuickPreview'),(''),'QuickPreview','QuickPreview','');\nFILE_SCHEMA(('CONFIG_CONTROL_DESIGN'));\nENDSEC;\nDATA;\n");
        step.AppendLine("#1=APPLICATION_CONTEXT('configuration controlled 3d designs of mechanical parts and assemblies');\n#2=APPLICATION_PROTOCOL_DEFINITION('international standard','config_control_design',1994,#1);\n#3=PRODUCT_CONTEXT('',#1,'mechanical');\n#4=PRODUCT('Cube','Cube','',(#3));\n#5=PRODUCT_DEFINITION_FORMATION('','',#4);\n#6=PRODUCT_DEFINITION_CONTEXT('part definition',#1,'design');\n#7=PRODUCT_DEFINITION('design','',#5,#6);\n#8=PRODUCT_DEFINITION_SHAPE('','',#7);\n#9=SHAPE_DEFINITION_REPRESENTATION(#8,#10);\n#10=FACETED_BREP_SHAPE_REPRESENTATION('',(#11),#12);\n#11=FACETED_BREP('Cube',#20);\n#12=(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#15))GLOBAL_UNIT_ASSIGNED_CONTEXT((#13,#14,#16))REPRESENTATION_CONTEXT('',''));\n#13=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));\n#14=(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.));\n#15=UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-6),#13,'distance_accuracy_value','');\n#16=(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT());");
        step.AppendLine("#20=CLOSED_SHELL('',("+string.Join(',',Enumerable.Range(0,6).Select(i=>"#"+(30+i*3)))+"));");
        for(var i=0;i<faces.Length;i++){var id=30+i*3;step.AppendLine($"#{id}=FACE('',(#{id+1}));\n#{id+1}=FACE_OUTER_BOUND('',#{id+2},.T.);\n#{id+2}=POLY_LOOP('',("+string.Join(',',faces[i].Select(v=>"#"+(100+v)))+"));");}
        for(var i=0;i<vertices.Length;i++){var(x,y,z)=vertices[i];step.AppendLine($"#{100+i}=CARTESIAN_POINT('',({x}.,{y}.,{z}.));");}
        step.Replace("FACETED_BREP_SHAPE_REPRESENTATION", "ADVANCED_BREP_SHAPE_REPRESENTATION");
        step.Replace("FACETED_BREP('Cube'", "MANIFOLD_SOLID_BREP('Cube'");
        var normals = new[] { "0.,0.,-1.", "0.,0.,1.", "0.,-1.,0.", "1.,0.,0.", "0.,1.,0.", "-1.,0.,0." };
        for (var index=0;index<faces.Length;index++)
        {
            var faceId=30+index*3;
            var planeId=300+index*4;
            step.Replace($"#{faceId}=FACE('',(#{faceId+1}));",$"#{faceId}=ADVANCED_FACE('',(#{faceId+1}),#{planeId},.T.);");
            var reference=index is 3 or 5 ? "0.,1.,0." : "1.,0.,0.";
            step.AppendLine($"#{planeId}=PLANE('',#{planeId+1});\n#{planeId+1}=AXIS2_PLACEMENT_3D('',#{100+faces[index][0]},#{planeId+2},#{planeId+3});\n#{planeId+2}=DIRECTION('',({normals[index]}));\n#{planeId+3}=DIRECTION('',({reference}));");
        }
        step.AppendLine("ENDSEC;\nEND-ISO-10303-21;");
        File.WriteAllText(Path.Combine(directory,"cube.step"),step.ToString());
        CreateOffsetExchangeFixtures(directory, step.ToString(), vertices);
        File.WriteAllText(Path.Combine(directory,"cube.ifc"),"ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('Original QuickPreview cube'),'2;1');\nFILE_NAME('cube.ifc','2026-11-16T00:00:00',('QuickPreview'),(''),'QuickPreview','QuickPreview','');\nFILE_SCHEMA(('IFC2X3'));\nENDSEC;\nDATA;\n#1=IFCPERSON($,$,'Test',$,$,$,$,$);\n#2=IFCORGANIZATION($,'QuickPreview',$,$,$);\n#3=IFCPERSONANDORGANIZATION(#1,#2,$);\n#4=IFCAPPLICATION(#2,'1','QuickPreview','QuickPreview');\n#5=IFCOWNERHISTORY(#3,#4,$,.ADDED.,$,$,$,0);\n#10=IFCCARTESIANPOINT((0.,0.,0.));\n#11=IFCDIRECTION((0.,0.,1.));\n#12=IFCDIRECTION((1.,0.,0.));\n#13=IFCAXIS2PLACEMENT3D(#10,#11,#12);\n#14=IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-5,#13,$);\n#15=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);\n#16=IFCUNITASSIGNMENT((#15));\n#17=IFCPROJECT('0JpYzZCFr4uwK5lQ4xeXYZ',#5,'Cube',$,$,$,$,(#14),#16);\n#20=IFCCARTESIANPOINT((0.,0.));\n#21=IFCAXIS2PLACEMENT2D(#20,$);\n#22=IFCRECTANGLEPROFILEDEF(.AREA.,'Cube',#21,20.,20.);\n#23=IFCEXTRUDEDAREASOLID(#22,#13,#11,20.);\n#24=IFCSHAPEREPRESENTATION(#14,'Body','SweptSolid',(#23));\n#25=IFCPRODUCTDEFINITIONSHAPE($,$,(#24));\n#26=IFCLOCALPLACEMENT($,#13);\n#27=IFCBUILDINGELEMENTPROXY('0JpYzZCFr4uwK5lQ4xeXY2',#5,'Cube',$,$,#26,#25,$,.ELEMENT.);\nENDSEC;\nEND-ISO-10303-21;\n");
    }

    private static void CreateOffsetExchangeFixtures(string directory, string cubeStep, (int X, int Y, int Z)[] vertices)
    {
        // A translated non-cubic solid exposes source/world center confusion that
        // a symmetric origin-centered cube cannot. STEP surface origins refer to
        // these same CARTESIAN_POINT entities, preserving a valid closed solid.
        var offsetStep = cubeStep;
        var obj = new StringBuilder("# Original offset, asymmetric QuickPreview part\n");
        for (var index = 0; index < vertices.Length; index++)
        {
            var (x, y, z) = vertices[index];
            var px = -420 + x * 106 / 20;
            var py = -80 + y;
            var pz = 310 + z * 53 / 20;
            offsetStep = offsetStep.Replace($"#{100 + index}=CARTESIAN_POINT('',({x}.,{y}.,{z}.));",
                $"#{100 + index}=CARTESIAN_POINT('',({px}.,{py}.,{pz}.));");
            obj.AppendLine($"v {px} {py} {pz}");
        }
        foreach (var face in new[] { "1 4 3 2", "5 6 7 8", "1 2 6 5", "2 3 7 6", "3 4 8 7", "4 1 5 8" })
            obj.AppendLine("f " + face);
        File.WriteAllText(Path.Combine(directory, "initial-offset.step"), offsetStep);
        File.WriteAllText(Path.Combine(directory, "initial-offset.obj"), obj.ToString());

        // Exercise 3MF's per-build-item transform, not just baked-in mesh vertices:
        // rotate, scale non-uniformly, translate and use a non-default model unit.
        using var original = ZipFile.OpenRead(Path.Combine(directory, "cube.3mf"));
        using var targetFile = File.Create(Path.Combine(directory, "initial-transformed.3mf"));
        using var target = new ZipArchive(targetFile, ZipArchiveMode.Create);
        foreach (var entry in original.Entries)
        {
            using var input = new StreamReader(entry.Open());
            var text = input.ReadToEnd();
            if (entry.FullName == "3D/3dmodel.model")
                text = text.Replace("unit=\"millimeter\"", "unit=\"inch\"")
                    .Replace("<item objectid=\"1\"/>", "<item objectid=\"1\" transform=\"0 1 0 -2 0 0 0 0 3 120 -75 43\"/>");
            using var output = new StreamWriter(target.CreateEntry(entry.FullName).Open());
            output.Write(text);
        }
    }

    private static void WriteUnitCubeObj(string path, double halfExtent)
    {
        var vertices = new[] { (-1,-1,-1),(1,-1,-1),(1,1,-1),(-1,1,-1),(-1,-1,1),(1,-1,1),(1,1,1),(-1,1,1) };
        var text = new StringBuilder("# Original scale-invariant model fixture\n");
        foreach (var (x, y, z) in vertices)
            text.AppendLine(FormattableString.Invariant($"v {x * halfExtent:R} {y * halfExtent:R} {z * halfExtent:R}"));
        foreach (var face in new[] { "1 4 3 2", "5 6 7 8", "1 2 6 5", "2 3 7 6", "3 4 8 7", "4 1 5 8" })
            text.AppendLine("f " + face);
        File.WriteAllText(path, text.ToString());
    }

    private static void WriteBinarySlab(string path, float x, float y, float z)
    {
        // Original asymmetric Z-up part, matching the dimensions of the reported
        // failure. Signed/large offsets catch source-vs-render coordinate mistakes.
        var vertices = new[] { (x,y,z),(x+106,y,z),(x+106,y+20,z),(x,y+20,z),
            (x,y,z+106),(x+106,y,z+106),(x+106,y+20,z+106),(x,y+20,z+106) };
        var faces = new[] { new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,1,5,4},new[]{1,2,6,5},new[]{2,3,7,6},new[]{3,0,4,7} };
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(new byte[80]); writer.Write(12u);
        foreach (var face in faces)
        foreach (var triangle in new[] { new[]{face[0],face[1],face[2]},new[]{face[0],face[2],face[3]} })
        {
            writer.Write(0f); writer.Write(0f); writer.Write(0f);
            foreach (var index in triangle) { var (px,py,pz)=vertices[index];writer.Write(px);writer.Write(py);writer.Write(pz); }
            writer.Write((ushort)0);
        }
    }

    private static void Assert(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);}
}
